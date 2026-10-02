using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Te1000Daemon
{
    // Edits the System Manager .tsproj on disk while XAE does not hold it, for
    // configuration that has no working Automation Interface write (TF3500 keeps
    // its own model and republishes it over every ConsumeXml). Proven live
    // 2026-10-01: with the solution saved, a tsproj edited while its project is
    // not loaded is read back by XAE/TF3500 on load.
    //
    // Mode "unload": Solution Explorer Project.UnloadProject, edit, ReloadProject.
    // Mode "reopen": Solution.Close(false) (everything is saved), edit, Open.
    // The caller's edit is a pure text -> text function; it runs on the file's
    // decoded text and must change only the bytes it means to.
    internal static class OfflineTsproj
    {
        private const string SolutionExplorerKind = "{3AE79031-E1BC-11D0-8F78-00A0C9110057}";
        private const int SelectItem = 1; // vsUISelectionTypeSelect

        internal sealed class Plan
        {
            public string SolutionPath;
            public string TsprojPath;
            public string ProjectName;
            public object Project;
            public List<string> Unsaved = new List<string>();
            public string Mode;
        }

        // Read-only: locates the System Manager project, lists anything unsaved and
        // picks the mode (selecting the project node in Solution Explorer).
        public static Plan Prepare(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            var plan = new Plan();
            plan.SolutionPath = ComHelpers.SafeStr(delegate { return dte.Solution.FullName; });
            if (string.IsNullOrWhiteSpace(plan.SolutionPath)) throw new BridgeException("No solution is open");
            NoteUnsaved(plan, "solution " + plan.SolutionPath, delegate { return dte.Solution.Saved; });

            // Project.Saved is not authoritative for the .tsproj: live, it read
            // Saved=true while its Project.Save still wrote the nested .plcproj.
            // Apply therefore saves the .tsproj project (and with it the nested PLC
            // projects) before unloading or closing, so it is not checked here;
            // every other project still has to be saved by the user.
            var all = new List<dynamic>();
            XaeActions.CollectProjects(dte.Solution.Projects, all);
            var tsprojs = new List<dynamic>();
            foreach (dynamic p in all)
            {
                string full = ComHelpers.SafeStr(delegate { return p.FullName; });
                if (full != null && full.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase)) { tsprojs.Add(p); continue; }
                NoteUnsaved(plan, "project " + (ComHelpers.SafeStr(delegate { return p.UniqueName; }) ?? full), delegate { return p.Saved; });
            }
            int docs = ComHelpers.SafeInt(delegate { return dte.Documents.Count; });
            for (int i = 1; i <= docs; i++)
            {
                dynamic d = null;
                try { d = dte.Documents.Item(i); } catch { }
                if (d != null) NoteUnsaved(plan, "document " + ComHelpers.SafeStr(delegate { return d.FullName; }), delegate { return d.Saved; });
            }
            if (tsprojs.Count != 1)
                throw new BridgeException(tsprojs.Count.ToString(CultureInfo.InvariantCulture) + " System Manager (.tsproj) projects are loaded; an offline edit needs exactly one.");
            dynamic sys = tsprojs[0];
            plan.Project = sys;
            plan.TsprojPath = ComHelpers.SafeStr(delegate { return sys.FullName; });
            plan.ProjectName = ComHelpers.SafeStr(delegate { return sys.Name; });
            plan.Mode = SelectProject(dte, plan.ProjectName) && CommandAvailable(dte, "Project.UnloadProject") ? "unload" : "reopen";
            return plan;
        }

        public static void AssertSaved(Plan plan)
        {
            if (plan.Unsaved.Count == 0) return;
            throw new BridgeException("Save these in XAE first; an offline .tsproj edit saves only the System Manager project (and its nested PLC projects) before it unloads or closes. Unsaved: " +
                string.Join("; ", plan.Unsaved.ToArray()));
        }

        // Backs the file up, takes it away from XAE, applies edit, and loads it
        // again whatever happened. A failed edit writes nothing; a failed write
        // restores the backup. Returns the live System Manager after the load.
        public static dynamic Apply(ActionContext ctx, Plan plan, Func<string, string> edit, Json.JObj result)
        {
            AssertSaved(plan);
            dynamic dte = ctx.Dte(true);
            // Save the .tsproj project first (it also writes the nested .plcproj and
            // changed PLC files, verified live): reopen mode's Solution.Close(false)
            // would drop them, and the edit must start from the saved file.
            dynamic sys = plan.Project;
            Json.JObj stampBefore = XaeActions.FileStamp(plan.TsprojPath);
            try { sys.Save(""); }
            catch (Exception ex) { throw new BridgeException("Project.Save failed for '" + plan.TsprojPath + "' (nothing unloaded or edited): " + ex.Message); }
            result["savedBeforeClose"] = true;
            result["tsprojBeforeSave"] = stampBefore;
            result["tsprojAfterSave"] = XaeActions.FileStamp(plan.TsprojPath);
            byte[] original = File.ReadAllBytes(plan.TsprojPath);
            string backup = plan.TsprojPath + ".te1000-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".bak";
            File.Copy(plan.TsprojPath, backup, false);
            result["tsproj"] = plan.TsprojPath;
            result["backup"] = backup;
            result["mode"] = plan.Mode;

            Exception failure = null;
            result["fileWritten"] = false;
            try
            {
                if (plan.Mode == "unload")
                {
                    if (!RunOnProjectNode(dte, plan.ProjectName, "Project.UnloadProject"))
                        throw new BridgeException("Project '" + plan.ProjectName + "' or its Unload Project command is not available in Solution Explorer");
                    if (IsLoaded(dte, plan.TsprojPath))
                        throw new BridgeException("Project.UnloadProject ran but '" + plan.ProjectName + "' is still loaded");
                }
                else
                {
                    dte.Solution.Close(false);
                }
                bool bom;
                string edited = edit(Decode(original, out bom));
                try { File.WriteAllBytes(plan.TsprojPath, Encode(edited, bom)); }
                catch { File.WriteAllBytes(plan.TsprojPath, original); throw; }
                result["fileWritten"] = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // Load it again whatever happened above.
            if (plan.Mode == "unload")
            {
                if (!RunOnProjectNode(dte, plan.ProjectName, "Project.ReloadProject") && !IsLoaded(dte, plan.TsprojPath))
                {
                    // No reload command for the node: reopen; everything was saved before the unload.
                    dte.Solution.Close(false);
                    dte.Solution.Open(plan.SolutionPath);
                    result["reloadedBy"] = "reopen";
                }
            }
            else if (!SolutionOpen(dte))
            {
                dte.Solution.Open(plan.SolutionPath);
            }

            ctx.Session.MarkStale();
            ctx.Cache.Clear();
            dynamic sm = WaitForSysManager(ctx);
            if (failure != null)
            {
                throw new BridgeException("Offline .tsproj edit failed, file left unchanged and project reloaded: " + failure.Message);
            }
            return sm;
        }

        // ---- text editing --------------------------------------------------

        // An element's extent in the raw text. Start: '<'; StartTagEnd: after the
        // start tag's '>'; ContentEnd: the closing tag's '<' (StartTagEnd when
        // Empty); End: after the closing '>'.
        internal sealed class Span
        {
            public string Name;
            public int Start, StartTagEnd, ContentEnd, End;
            public bool Empty;
        }

        public static string Decode(byte[] b, out bool bom)
        {
            bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            string t = new UTF8Encoding(false, true).GetString(b, bom ? 3 : 0, b.Length - (bom ? 3 : 0));
            Match m = Regex.Match(t.Substring(0, Math.Min(t.Length, 200)), "^<\\?xml[^>]*encoding=[\"']([^\"']+)[\"']");
            if (m.Success && !string.Equals(m.Groups[1].Value, "utf-8", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("The .tsproj declares encoding '" + m.Groups[1].Value + "'; offline edits support UTF-8 only.");
            return t;
        }

        public static byte[] Encode(string t, bool bom)
        {
            byte[] body = new UTF8Encoding(false, true).GetBytes(t);
            if (!bom) return body;
            var r = new byte[body.Length + 3];
            r[0] = 0xEF; r[1] = 0xBB; r[2] = 0xBF;
            Buffer.BlockCopy(body, 0, r, 3, body.Length);
            return r;
        }

        // The single element named name in the text (e.g. Analytics).
        public static Span Root(string t, string name)
        {
            int at = -1;
            int from = 0;
            for (;;)
            {
                int i = t.IndexOf("<" + name, from, StringComparison.Ordinal);
                if (i < 0) break;
                int j = i + name.Length + 1;
                if (j < t.Length && IsNameEnd(t[j]))
                {
                    if (at >= 0) throw new BridgeException("More than one <" + name + "> element in the .tsproj");
                    at = i;
                }
                from = j;
            }
            if (at < 0) throw new BridgeException("No <" + name + "> element in the .tsproj");
            return ReadElement(t, at);
        }

        // The ordinal-th direct child of parent named name.
        public static Span Child(string t, Span parent, string name, int ordinal)
        {
            int seen = 0;
            foreach (Span c in Children(t, parent))
            {
                if (c.Name != name) continue;
                if (seen == ordinal) return c;
                seen++;
            }
            throw new BridgeException("No <" + name + "> #" + ordinal.ToString(CultureInfo.InvariantCulture) + " under <" + parent.Name + "> in the .tsproj");
        }

        // Removes the element; when it sits alone on its line, the line goes too.
        public static string RemoveElement(string t, Span e)
        {
            int start = e.Start;
            int end = e.End;
            int ls = t.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            if (t.Substring(ls, start - ls).Trim().Length == 0)
            {
                start = ls;
                if (string.CompareOrdinal(t, end, "\r\n", 0, 2) == 0) end += 2;
                else if (end < t.Length && t[end] == '\n') end += 1;
            }
            return t.Substring(0, start) + t.Substring(end);
        }

        // Replaces a leaf element's text, keeping its start tag as written.
        public static string SetLeaf(string t, Span e, string value)
        {
            string escaped = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            if (e.Empty)
            {
                if (value.Length == 0) return t;
                string tag = t.Substring(e.Start, e.StartTagEnd - e.Start);
                string open = tag.Substring(0, tag.LastIndexOf('/')).TrimEnd() + ">";
                return t.Substring(0, e.Start) + open + escaped + "</" + e.Name + ">" + t.Substring(e.End);
            }
            if (t.IndexOf('<', e.StartTagEnd, e.ContentEnd - e.StartTagEnd) >= 0)
                throw new BridgeException("<" + e.Name + "> is not a leaf element");
            return t.Substring(0, e.StartTagEnd) + escaped + t.Substring(e.ContentEnd);
        }

        // Sets an attribute the start tag already carries, keeping its position and
        // quote; value null removes it with its leading whitespace.
        public static string SetAttribute(string t, Span e, string name, string value)
        {
            string tag = t.Substring(e.Start, e.StartTagEnd - e.Start);
            Match m = Regex.Match(tag, "(\\s+)" + Regex.Escape(name) + "\\s*=\\s*(\"[^\"]*\"|'[^']*')");
            if (!m.Success) throw new BridgeException("<" + e.Name + "> has no " + name + " attribute in the .tsproj");
            string repl = "";
            if (value != null)
            {
                char q = m.Groups[2].Value[0];
                repl = m.Groups[1].Value + name + "=" + q + value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(q.ToString(), q == '"' ? "&quot;" : "&apos;") + q;
            }
            return t.Substring(0, e.Start) + tag.Substring(0, m.Index) + repl + tag.Substring(m.Index + m.Length) + t.Substring(e.StartTagEnd);
        }

        // Inserts element text on its own line after the element, at its indentation.
        public static string InsertAfter(string t, Span after, string element)
        {
            int ls = t.LastIndexOf('\n', Math.Max(0, after.Start - 1)) + 1;
            string indent = t.Substring(ls, after.Start - ls);
            if (indent.Trim().Length != 0) throw new BridgeException("<" + after.Name + "> does not start its own line in the .tsproj");
            string nl = t.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            return t.Substring(0, after.End) + nl + indent + element + t.Substring(after.End);
        }

        // The edited text must parse to exactly the original document with plan
        // applied; anything else (a mis-scanned span, a stray byte) is refused.
        public static void AssertPlanned(string original, string edited, Action<XmlDocument> plan)
        {
            XmlDocument want = LoadDom(original);
            plan(want);
            XmlDocument got = LoadDom(edited);
            if (want.OuterXml != got.OuterXml) throw new BridgeException("The text edit does not match the planned change; nothing was written.");
        }

        public static XmlDocument LoadDom(string text)
        {
            var d = new XmlDocument();
            d.XmlResolver = null;
            d.LoadXml(text);
            return d;
        }

        public static List<Span> Children(string t, Span e)
        {
            var l = new List<Span>();
            if (e.Empty) return l;
            int pos = e.StartTagEnd;
            while (pos < e.ContentEnd)
            {
                int n = t.IndexOf('<', pos, e.ContentEnd - pos);
                if (n < 0) break;
                int skip = SkipMarkup(t, n);
                if (skip > 0) { pos = skip; continue; }
                Span c = ReadElement(t, n);
                l.Add(c);
                pos = c.End;
            }
            return l;
        }

        public static Span ReadElement(string t, int lt)
        {
            var s = new Span();
            s.Start = lt;
            int i = lt + 1;
            while (i < t.Length && !IsNameEnd(t[i])) i++;
            s.Name = t.Substring(lt + 1, i - lt - 1);
            int gt = TagEnd(t, i);
            s.StartTagEnd = gt + 1;
            s.Empty = t[gt - 1] == '/';
            if (s.Empty) { s.ContentEnd = s.StartTagEnd; s.End = s.StartTagEnd; return s; }
            int pos = s.StartTagEnd;
            for (;;)
            {
                int n = t.IndexOf('<', pos);
                if (n < 0) throw new BridgeException("Unterminated <" + s.Name + "> in the .tsproj");
                int skip = SkipMarkup(t, n);
                if (skip > 0) { pos = skip; continue; }
                if (n + 1 < t.Length && t[n + 1] == '/')
                {
                    int g = t.IndexOf('>', n);
                    string closing = t.Substring(n + 2, g - n - 2).Trim();
                    if (closing != s.Name) throw new BridgeException("<" + s.Name + "> is closed by </" + closing + "> in the .tsproj");
                    s.ContentEnd = n;
                    s.End = g + 1;
                    return s;
                }
                pos = ReadElement(t, n).End;
            }
        }

        // Index after a comment, CDATA section or processing instruction at n; 0 if none.
        private static int SkipMarkup(string t, int n)
        {
            string close = null;
            if (string.CompareOrdinal(t, n, "<!--", 0, 4) == 0) close = "-->";
            else if (string.CompareOrdinal(t, n, "<![CDATA[", 0, 9) == 0) close = "]]>";
            else if (string.CompareOrdinal(t, n, "<?", 0, 2) == 0) close = "?>";
            if (close == null) return 0;
            int e = t.IndexOf(close, n, StringComparison.Ordinal);
            if (e < 0) throw new BridgeException("Unterminated markup in the .tsproj");
            return e + close.Length;
        }

        private static int TagEnd(string t, int i)
        {
            char q = '\0';
            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (q != '\0') { if (c == q) q = '\0'; }
                else if (c == '"' || c == '\'') q = c;
                else if (c == '>') return i;
            }
            throw new BridgeException("Unterminated start tag in the .tsproj");
        }

        private static bool IsNameEnd(char c) { return char.IsWhiteSpace(c) || c == '/' || c == '>'; }

        // ---- XAE helpers -----------------------------------------------------

        // Saved false, or not readable at all, both block the edit.
        private static void NoteUnsaved(Plan plan, string what, Func<object> saved)
        {
            object v = ComHelpers.Safe<object>(saved);
            if (!(v is bool)) plan.Unsaved.Add(what + " (Saved not readable)");
            else if (!(bool)v) plan.Unsaved.Add(what);
        }

        private static bool CommandAvailable(dynamic dte, string name)
        {
            try { return (bool)dte.Commands.Item(name).IsAvailable; } catch { return false; }
        }

        // Selects the project's node and runs a Solution Explorer project command
        // (Project.UnloadProject / Project.ReloadProject) on it; false, with nothing
        // run, when the node is not found or the command is not available for it.
        internal static bool RunOnProjectNode(dynamic dte, string projectName, string command)
        {
            if (!SelectProject(dte, projectName) || !CommandAvailable(dte, command)) return false;
            dte.ExecuteCommand(command);
            return true;
        }

        // Selects the project's node (also when shown as "<name> (unloaded)").
        private static bool SelectProject(dynamic dte, string projectName)
        {
            try { dte.Windows.Item(SolutionExplorerKind).Activate(); } catch { return false; }
            dynamic root = null;
            try { root = dte.ToolWindows.SolutionExplorer.UIHierarchyItems.Item(1); } catch { return false; }
            int n = ComHelpers.SafeInt(delegate { return root.UIHierarchyItems.Count; });
            for (int i = 1; i <= n; i++)
            {
                dynamic item = null;
                try { item = root.UIHierarchyItems.Item(i); } catch { }
                string name = item == null ? null : ComHelpers.SafeStr(delegate { return item.Name; });
                if (name == null || (name != projectName && !name.StartsWith(projectName + " (", StringComparison.Ordinal))) continue;
                item.Select(SelectItem);
                return true;
            }
            return false;
        }

        private static bool SolutionOpen(dynamic dte)
        {
            object v = ComHelpers.Safe<object>(delegate { return dte.Solution.IsOpen; });
            return v is bool && (bool)v;
        }

        // True only when a project with this file still exposes its Object; an
        // unloaded project (or one Projects.Item cannot return) is not loaded.
        internal static bool IsLoaded(dynamic dte, string file)
        {
            var all = new List<dynamic>();
            try { XaeActions.CollectProjects(dte.Solution.Projects, all); }
            catch (BridgeException) { return false; }
            foreach (dynamic p in all)
            {
                string full = ComHelpers.SafeStr(delegate { return p.FullName; });
                if (full == null || !string.Equals(full, file, StringComparison.OrdinalIgnoreCase)) continue;
                if (ComHelpers.Safe<object>(delegate { return (object)p.Object; }) != null) return true;
            }
            return false;
        }

        // Waits until the reloaded System Manager answers with a TIAN node.
        private static dynamic WaitForSysManager(ActionContext ctx)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(180);
            Exception last = null;
            while (DateTime.UtcNow < until)
            {
                try
                {
                    dynamic sm = ctx.SysManager();
                    ComHelpers.GetTreeItem(sm, "TIAN");
                    return sm;
                }
                catch (Exception ex) { last = ex; }
                System.Threading.Thread.Sleep(1000);
            }
            throw new BridgeException("The System Manager did not come back within 180 s after the offline edit: " + (last == null ? "" : last.Message));
        }
    }
}
