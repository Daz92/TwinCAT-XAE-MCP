using System;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using TCatSysManagerLib;

namespace Te1000Daemon
{
    // twincat_module_* and twincat_cpp_* actions.
    //
    // module actions operate under TIRC^TcCOM Objects (create/get_xml/set_xml/
    // enable_symbols) or via the typed ITcModuleManager3 enumeration (list) and
    // ITcModuleInstance2.SetModuleContext (set_context). cpp actions create/open
    // C++ projects under TIXC, drive TMC codegen / publish via ConsumeXml, and
    // build a named C++ project via Solution.SolutionBuild.BuildProject.
    //
    // module_list / module_set_context need the vtable-only typed COM interfaces
    // (ITcSysManager4 / ITcModuleManager3 / ITcModuleInstance2) that late-bound
    // dynamic cannot QI — these mirror the PS bridge's compiled Te1000ModuleHelper
    // (L1121-1157). The TCatSysManagerLib reference is EmbedInteropTypes=true.
    //
    // index.js enforces the confirm tokens for module_set_context
    // (ALLOW_TWINCAT_MODULE_CONTEXT) and cpp_publish (ALLOW_CPP_PUBLISH) BEFORE
    // calling the bridge; the PS handlers do NOT re-check, so neither do we.
    //
    // C#5-clean (no interpolation, no out var, no expression-bodied members).
    internal static class ModuleCppActions
    {
        public static void Register(Dictionary<string, ActionHandler> h)
        {
            h["twincat_module_list"] = ModuleList;
            h["twincat_module_create"] = ModuleCreate;
            h["twincat_module_get_xml"] = ModuleGetXml;
            h["twincat_module_set_xml"] = ModuleSetXml;
            h["twincat_module_enable_symbols"] = ModuleEnableSymbols;
            h["twincat_module_set_context"] = ModuleSetContext;
            h["twincat_module_reload_tmc"] = ModuleReloadTmc;
            h["twincat_module_delete_unlinked"] = ModuleDeleteUnlinked;
            h["twincat_cpp_create_project"] = CppCreateProject;
            h["twincat_cpp_create_module"] = CppCreateModule;
            h["twincat_cpp_open"] = CppOpen;
            h["twincat_cpp_consume_xml"] = CppConsumeXml;
            h["twincat_cpp_set_props"] = CppSetProps;
            h["twincat_cpp_build_project"] = CppBuildProject;
            h["twincat_cpp_publish"] = CppPublish;
        }

        // ---- twincat_module_list (L8941-8969) --------------------------------
        // Port of Te1000ModuleHelper.List (L1130-1148): enumerate ITcModuleInstance2
        // under the module manager. There is no 'ObjectId' member -> objectId mirrors
        // oid. An empty cell yields an empty list (not an error).
        private static Json.JObj ModuleList(ActionContext ctx)
        {
            dynamic sm = ctx.SysManager();
            var modules = new Json.JArr();
            string step = "GetModuleManager";
            try
            {
                ITcSysManager4 typedSm = (ITcSysManager4)sm;
                ITcModuleManager3 mgr = (ITcModuleManager3)typedSm.GetModuleManager();
                // The manager's own enumerator (IEnumerable / _NewEnum) fails with
                // DISP_E_MEMBERNOTFOUND through the embedded interop, so the
                // Modules collection is indexed instead (base probed: 0 or 1).
                step = "Modules";
                ITcModuleInstanceCollection col = mgr.Modules;
                int count = col.Count;
                var seen = new HashSet<uint>();
                for (int i = 0; i <= count; i++)
                {
                    ITcModuleInstance2 mi;
                    step = "Modules.Item(" + i.ToString(CultureInfo.InvariantCulture) + ")";
                    try { mi = col[i] as ITcModuleInstance2; }
                    catch (Exception) { if (i == 0 || i == count) continue; throw; }
                    if (mi == null) continue;

                    var m = new Json.JObj();
                    step = "oid"; uint oid = mi.oid;
                    if (!seen.Add(oid)) continue;
                    step = "ModuleTypeName"; m["moduleTypeName"] = mi.ModuleTypeName;
                    step = "ModuleInstanceName"; m["moduleInstanceName"] = mi.ModuleInstanceName;
                    step = "ClassID"; m["classId"] = mi.ClassID.ToString();
                    m["oid"] = (long)oid;
                    m["objectId"] = (long)oid;
                    step = "ParentOID"; m["parentOid"] = (long)mi.ParentOID;
                    modules.Add(m);
                }
            }
            catch (Exception ex) { throw new BridgeException("module list failed at " + step + ": " + ex.Message); }

            var data = new Json.JObj();
            data["count"] = modules.Count;
            data["modules"] = modules;
            return data;
        }

        // ---- twincat_module_create (L8971-8996) ------------------------------
        private static Json.JObj ModuleCreate(ActionContext ctx)
        {
            string parentPath = "TIRC^TcCOM Objects";
            string name = ctx.Payload.Str("name");
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("name is required");
            string by = ctx.Payload.Str("by");
            if (by != "classid" && by != "name") throw new BridgeException("by must be 'classid' or 'name'");
            string id = ctx.Payload.Str("id");
            if (string.IsNullOrWhiteSpace(id)) throw new BridgeException("id is required");
            int subType = (by == "classid") ? 0 : 1;
            string before = ctx.Payload.Truthy("before") ? ctx.Payload.Str("before") : "";

            dynamic sm = ctx.SysManager();
            dynamic parent = ComHelpers.GetTreeItem(sm, parentPath);
            dynamic child = parent.CreateChild(name, subType, before, id);
            AssertWellFormedChild(parent, child, name, subType, parentPath);

            ctx.Cache.Invalidate(parentPath);

            var data = new Json.JObj();
            data["parentPath"] = parentPath;
            data["child"] = ComHelpers.ConvertTreeItem(child);
            return data;
        }

        // ---- twincat_module_get_xml (L8998-9012) -----------------------------
        private static Json.JObj ModuleGetXml(ActionContext ctx)
        {
            string path = ctx.Payload.Str("path");
            if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("path is required");

            dynamic sm = ctx.SysManager();
            dynamic item = ComHelpers.GetTreeItem(sm, path);
            string xml = ComHelpers.StripTreeImage(ComHelpers.ProduceXml(item));

            var data = new Json.JObj();
            data["treePath"] = path;
            data["xml"] = xml;
            return data;
        }

        // ---- twincat_module_set_xml (L9014-9035) -----------------------------
        private static Json.JObj ModuleSetXml(ActionContext ctx)
        {
            string path = ctx.Payload.Str("path");
            string xml = ctx.Payload.Str("xml");
            if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("path is required");
            if (string.IsNullOrWhiteSpace(xml)) throw new BridgeException("xml is required");
            bool returnXml = ctx.Payload.Has("returnXml") && ctx.Payload.Bool("returnXml");

            dynamic sm = ctx.SysManager();
            dynamic item = SetTreeItemXmlInternal(ctx, sm, path, xml);

            var data = new Json.JObj();
            data["treePath"] = path;
            if (returnXml)
            {
                data["xml"] = ComHelpers.StripTreeImage(ComHelpers.ProduceXml(item));
            }
            return data;
        }

        // ---- twincat_module_enable_symbols (L9037-9088) ----------------------
        // Read ProduceXml, set CreateSymbol / CreateSymbols attributes on the
        // requested parameter / data-area nodes, ConsumeXml back if changed.
        private static Json.JObj ModuleEnableSymbols(ActionContext ctx)
        {
            string path = ctx.Payload.Str("path");
            if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("path is required");
            bool doParams = ctx.Payload.Has("parameters") && ctx.Payload.Bool("parameters");
            bool doAreas = ctx.Payload.Has("dataAreas") && ctx.Payload.Bool("dataAreas");
            bool returnXml = ctx.Payload.Has("returnXml") && ctx.Payload.Bool("returnXml");

            dynamic sm = ctx.SysManager();
            dynamic item = ComHelpers.GetTreeItem(sm, path);

            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
            doc.LoadXml(ComHelpers.ProduceXml(item));
            bool changed = false;

            if (doParams)
            {
                System.Xml.XmlNodeList nodes = doc.SelectNodes("//Parameters//Parameter");
                if (nodes != null)
                {
                    foreach (System.Xml.XmlNode n in nodes)
                    {
                        System.Xml.XmlElement el = n as System.Xml.XmlElement;
                        if (el == null) continue;
                        el.SetAttribute("CreateSymbol", "true");
                        changed = true;
                    }
                }
            }
            if (doAreas)
            {
                System.Xml.XmlNodeList nodes = doc.SelectNodes("//DataAreas//DataArea/AreaNo");
                if (nodes != null)
                {
                    foreach (System.Xml.XmlNode n in nodes)
                    {
                        System.Xml.XmlElement el = n as System.Xml.XmlElement;
                        if (el == null) continue;
                        el.SetAttribute("CreateSymbols", "true");
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                ComHelpers.ConsumeXml(item, doc.OuterXml);
                ctx.Cache.Invalidate(path);
            }

            var data = new Json.JObj();
            data["treePath"] = path;
            data["parameters"] = doParams;
            data["dataAreas"] = doAreas;
            data["changed"] = changed;
            if (returnXml)
            {
                data["xml"] = ComHelpers.StripTreeImage(ComHelpers.ProduceXml(item));
            }
            return data;
        }

        // ---- twincat_module_set_context (L9090-9116) -------------------------
        // Port of Te1000ModuleHelper.SetContext (L1153-1156): typed
        // ITcModuleInstance2.SetModuleContext(contextId, taskObjectId) — both are
        // DECIMAL oids. Confirm token enforced upstream in index.js.
        private static Json.JObj ModuleSetContext(ActionContext ctx)
        {
            string path = ctx.Payload.Str("path");
            if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("path is required");
            if (!ctx.Payload.Has("taskObjectId")) throw new BridgeException("taskObjectId is required");
            int taskObjectId = ctx.Payload.Int("taskObjectId", 0);
            int contextId = (ctx.Payload.Has("contextId")) ? ctx.Payload.Int("contextId", 0) : 0;

            dynamic sm = ctx.SysManager();
            dynamic item = ComHelpers.GetTreeItem(sm, path);

            ((ITcModuleInstance2)item).SetModuleContext((uint)contextId, (uint)taskObjectId);

            ctx.Cache.Invalidate(path);

            var data = new Json.JObj();
            data["treePath"] = path;
            data["contextId"] = contextId;
            data["taskObjectId"] = taskObjectId;
            data["contextSet"] = true;
            return data;
        }

        // ---- twincat_module_reload_tmc ---------------------------------------
        // Offline TcCOM description reload (TE1000 "Reloading TMC files"):
        // ConsumeXml <TreeItem><ReloadTmc Path=".." Mode="warm">true</ReloadTmc>
        // </TreeItem> on the instance. Port of the wrapper's reloadTccomTmc:
        // pre-checks the instance identity (ClassID + current ClassFactoryId) and
        // the reviewed TMC (vendor|library|version, exactly one Module with the
        // ClassID, same module name), post-checks path/ClassID/ObjectId unchanged
        // and ClassFactoryId switched to the target, and reports every parameter
        // value, data area/symbol, context and mapping link that changed. Like
        // set_xml it is an unsaved offline edit (no confirm token); dryRun:true
        // runs the pre-checks only.
        private const string TccomRoot = "TIRC^TcCOM Objects";

        private static Json.JObj ModuleReloadTmc(ActionContext ctx)
        {
            string path = ctx.Require("modulePath");
            if (!path.StartsWith(TccomRoot + "^", StringComparison.Ordinal))
                throw new BridgeException("modulePath must be a TcCOM instance path below " + TccomRoot);
            string tmcPath = ctx.Require("tmcPath");
            Guid expectedClassId = ParseGuid(ctx.Require("expectedClassId"), "expectedClassId");
            string expectedCurrent = ctx.Require("expectedCurrentClassFactoryId");
            string expectedTarget = ctx.Require("expectedTargetClassFactoryId");
            bool dryRun = ctx.Payload.Bool("dryRun");
            if (expectedCurrent == expectedTarget)
                throw new BridgeException("expectedTargetClassFactoryId equals expectedCurrentClassFactoryId; a reload would prove nothing");
            if (!System.IO.Path.IsPathRooted(tmcPath) || !tmcPath.EndsWith(".tmc", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("tmcPath must be an absolute path to a .tmc file: " + tmcPath);
            tmcPath = System.IO.Path.GetFullPath(tmcPath);
            if (!System.IO.File.Exists(tmcPath)) throw new BridgeException("tmcPath does not exist: " + tmcPath);

            dynamic sm = ctx.SysManager();
            dynamic item = ComHelpers.GetTreeItem(sm, path);
            TccomSnapshot before = ReadTccomSnapshot(sm, item, path);
            if (before.ClassId != expectedClassId)
                throw new BridgeException("ClassID of '" + path + "' is " + before.ClassId.ToString("B") + ", expected " + expectedClassId.ToString("B") + " (nothing changed)");
            if (before.ClassFactoryId != expectedCurrent)
                throw new BridgeException("ClassFactoryId of '" + path + "' is '" + before.ClassFactoryId + "', expected current '" + expectedCurrent + "' (nothing changed)");
            Json.JObj tmc = AssertReloadTargetTmc(tmcPath, expectedClassId, expectedTarget, before.ModuleName);

            var data = new Json.JObj();
            data["treePath"] = path;
            data["tmcPath"] = tmcPath;
            data["mode"] = "warm";
            data["tmc"] = tmc;
            data["before"] = before.Identity();
            if (dryRun)
            {
                data["dryRun"] = true;
                data["wouldReload"] = true;
                return data;
            }

            string reloadXml = "<TreeItem><ReloadTmc Path=\"" + System.Security.SecurityElement.Escape(tmcPath) + "\" Mode=\"warm\">true</ReloadTmc></TreeItem>";
            try { ComHelpers.ConsumeXml(item, reloadXml); }
            catch (Exception ex) { throw new BridgeException("ReloadTmc ConsumeXml failed for '" + path + "': " + ex.Message); }
            ctx.Cache.Invalidate(path);

            dynamic reloaded = ComHelpers.GetTreeItem(sm, path);
            TccomSnapshot after = ReadTccomSnapshot(sm, reloaded, path);
            data["after"] = after.Identity();
            Json.JObj changes = DiffRecords(before.Records, after.Records);
            data["changes"] = changes;
            int linksLost = 0;
            foreach (object k in (Json.JArr)changes["removed"])
                if (((string)k).StartsWith("link:", StringComparison.Ordinal)) linksLost++;
            data["linksLost"] = linksLost;

            var problems = new List<string>();
            if (linksLost > 0) problems.Add(linksLost.ToString(CultureInfo.InvariantCulture) + " variable link(s) lost");
            if (after.Path != before.Path) problems.Add("path changed to '" + after.Path + "'");
            if (after.ClassId != before.ClassId) problems.Add("ClassID changed to " + after.ClassId.ToString("B"));
            if (after.ObjectId != before.ObjectId) problems.Add("ObjectId changed from " + before.ObjectId + " to " + after.ObjectId);
            if (after.ClassFactoryId == before.ClassFactoryId) problems.Add("ClassFactoryId unchanged ('" + before.ClassFactoryId + "')");
            else if (after.ClassFactoryId != expectedTarget) problems.Add("ClassFactoryId is '" + after.ClassFactoryId + "', not target '" + expectedTarget + "'");
            if (problems.Count > 0)
                throw new BridgeException("ReloadTmc was applied to '" + path + "' but did NOT succeed: " + string.Join("; ", problems.ToArray()) +
                    ". Nothing was saved; inspect the instance or close the solution without saving. Changes: " + Json.Write(data["changes"]));

            data["reloaded"] = true;
            return data;
        }

        // ---- twincat_module_delete_unlinked ----------------------------------
        // Port of the wrapper's deleteUnlinkedTccomInstance: delete one TcCOM
        // instance only when its ClassID matches and neither it nor any
        // descendant has a variable link — checked twice: every OwnerA/OwnerB in
        // ProduceMappingInfo, and the LinkActions <LinkedWith> walk (a walk that
        // runs out of budget refuses). An absent instance reports action:"absent".
        // ALLOW_TWINCAT_DELETE is enforced in index.js; dryRun:true checks only.
        private static Json.JObj ModuleDeleteUnlinked(ActionContext ctx)
        {
            string parentPath = ctx.Require("parentPath");
            if (parentPath != TccomRoot && !parentPath.StartsWith(TccomRoot + "^", StringComparison.Ordinal))
                throw new BridgeException("parentPath must be " + TccomRoot + " or a folder below it");
            string name = ctx.Require("instanceName");
            if (name.IndexOfAny(new char[] { '^', '\\', '/', '\r', '\n' }) >= 0) throw new BridgeException("instanceName must be a single tree-item name");
            Guid expectedClassId = ParseGuid(ctx.Require("expectedClassId"), "expectedClassId");
            bool dryRun = ctx.Payload.Bool("dryRun");
            string childPath = parentPath + "^" + name;

            dynamic sm = ctx.SysManager();
            dynamic parent = ComHelpers.GetTreeItem(sm, parentPath);
            dynamic item = ComHelpers.FindTreeItem(sm, childPath);
            var data = new Json.JObj();
            data["treePath"] = childPath;
            if (item == null) { data["action"] = "absent"; return data; }

            string actualName = ComHelpers.SafeStr(delegate { return item.Name; });
            string actualParent = ComHelpers.SafeStr(delegate { return item.Parent.PathName; });
            if (actualName != name || actualParent != parentPath)
                throw new BridgeException("'" + childPath + "' resolved to name '" + actualName + "' under '" + actualParent + "'; refusing (nothing deleted)");
            System.Xml.XmlElement module = TccomModuleDefinition(ComHelpers.ProduceXml(item), childPath);
            Guid classId = ParseGuid(DirectText(module, "CLSID", childPath), "CLSID of '" + childPath + "'");
            if (classId != expectedClassId)
                throw new BridgeException("ClassID of '" + childPath + "' is " + classId.ToString("B") + ", expected " + expectedClassId.ToString("B") + " (nothing deleted)");

            List<string> links = MappingLinksTouching(sm, childPath);
            if (links.Count == 0)
            {
                // Second, independent proof (slow: ProduceXml per node), only when
                // the mapping table found nothing.
                int[] budget = new int[] { 5000 };
                Json.JArr walked = LinkActions.GetVariableLinksRecursive(sm, item, 0, 16, null, budget);
                if (budget[0] <= 0) throw new BridgeException("Link walk of '" + childPath + "' exceeded its node budget; cannot prove it unlinked (nothing deleted)");
                foreach (object o in walked)
                {
                    Json.JObj l = (Json.JObj)o;
                    links.Add(l.Str("varA") + " <-> " + l.Str("varB"));
                }
            }
            if (links.Count > 0)
            {
                var shown = links.GetRange(0, Math.Min(links.Count, 10));
                throw new BridgeException("'" + childPath + "' or a descendant has " + links.Count.ToString(CultureInfo.InvariantCulture) +
                    " variable link(s); refusing (nothing deleted). First: " + string.Join("; ", shown.ToArray()));
            }

            data["classId"] = classId.ToString("B");
            data["linkCount"] = 0;
            if (dryRun)
            {
                data["dryRun"] = true;
                data["wouldDelete"] = true;
                return data;
            }

            try { parent.DeleteChild(name); }
            catch (Exception ex) { throw new BridgeException("DeleteChild failed for '" + childPath + "': " + ex.Message); }
            ctx.Cache.Invalidate(parentPath);
            ctx.Cache.InvalidateEnum();
            if (ComHelpers.FindTreeItem(sm, childPath) != null)
                throw new BridgeException("'" + childPath + "' still resolves after DeleteChild");
            data["action"] = "deleted";
            return data;
        }

        // ---- TcCOM snapshot helpers (reload_tmc / delete_unlinked) ------------

        private sealed class TccomSnapshot
        {
            public string Path, ClassFactoryId, ObjectId, ModuleName;
            public Guid ClassId;
            // key -> value: "param:<n>", "area:<n>", "symbol:<area>^<n>",
            // "context:<id>", "link:<ownerA> | <ownerB> | <varA> | <varB>".
            public SortedDictionary<string, string> Records = new SortedDictionary<string, string>(StringComparer.Ordinal);

            public Json.JObj Identity()
            {
                var o = new Json.JObj();
                o["treePath"] = Path;
                o["classId"] = ClassId.ToString("B");
                o["classFactoryId"] = ClassFactoryId;
                o["objectId"] = ObjectId;
                o["moduleName"] = ModuleName;
                o["recordCount"] = Records.Count;
                return o;
            }
        }

        private static TccomSnapshot ReadTccomSnapshot(dynamic sm, dynamic item, string path)
        {
            var s = new TccomSnapshot();
            s.Path = ComHelpers.SafeStr(delegate { return item.PathName; });
            if (s.Path != path) throw new BridgeException("TcCOM instance path is '" + s.Path + "', expected '" + path + "'");
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(ComHelpers.ProduceXml(item));
            System.Xml.XmlElement module = TccomModuleDefinition(doc, path);
            s.ClassId = ParseGuid(DirectText(module, "CLSID", path), "CLSID of '" + path + "'");
            s.ClassFactoryId = module.GetAttribute("ClassFactoryId").Trim();
            if (s.ClassFactoryId.Length == 0)
                s.ClassFactoryId = ((System.Xml.XmlElement)module.ParentNode).GetAttribute("ClassFactoryId").Trim();
            if (s.ClassFactoryId.Length == 0) throw new BridgeException("TcCOM instance '" + path + "' has no ClassFactoryId");
            s.ObjectId = DirectText(doc.DocumentElement, "ObjectId", path);
            s.ModuleName = DirectText(module, "Name", path);

            foreach (System.Xml.XmlElement v in module.SelectNodes("ParameterValues/Value"))
            {
                string n = DirectText(v, "Name", path);
                var payload = new System.Text.StringBuilder();
                foreach (System.Xml.XmlNode c in v.ChildNodes)
                    if (c is System.Xml.XmlElement && c.LocalName != "Name") payload.Append(c.OuterXml);
                s.Records["param:" + n] = payload.ToString();
            }
            foreach (System.Xml.XmlElement a in module.SelectNodes("DataAreas/DataArea"))
            {
                string n = DirectText(a, "Name", path);
                System.Xml.XmlElement areaNo = a["AreaNo"];
                s.Records["area:" + n] = (areaNo == null ? "" : areaNo.InnerText.Trim() + " type=" + areaNo.GetAttribute("AreaType") + " disabled=" + areaNo.GetAttribute("Disabled")) +
                    " ctx=" + OptText(a, "ContextId") + " bytes=" + OptText(a, "ByteSize");
                foreach (System.Xml.XmlElement sym in a.SelectNodes("Symbol"))
                    s.Records["symbol:" + n + "^" + OptText(sym, "Name")] = "disabled=" + sym.GetAttribute("Disabled") +
                        " bits=" + OptText(sym, "BitSize") + " offs=" + OptText(sym, "BitOffs");
            }
            foreach (System.Xml.XmlElement c in module.SelectNodes("Contexts/Context"))
                s.Records["context:" + OptText(c, "Id")] = "otcid=" + OptText(c, "ManualConfig/OTCID") + " prio=" + OptText(c, "Priority") +
                    " cycle=" + OptText(c, "CycleTime") + " dependOn=" + OptText(c, "DependOn");
            foreach (string l in MappingLinksTouching(sm, path)) s.Records["link:" + l] = "";
            return s;
        }

        // /TreeItem/TcModuleInstance/Module, or its single TmcDesc when present.
        private static System.Xml.XmlElement TccomModuleDefinition(string xml, string path)
        {
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);
            return TccomModuleDefinition(doc, path);
        }

        private static System.Xml.XmlElement TccomModuleDefinition(System.Xml.XmlDocument doc, string path)
        {
            System.Xml.XmlNodeList modules = doc.SelectNodes("/TreeItem/TcModuleInstance/Module");
            if (modules.Count != 1) throw new BridgeException("'" + path + "' is not a TcCOM instance (no single TcModuleInstance/Module)");
            System.Xml.XmlNodeList descs = modules[0].SelectNodes("TmcDesc");
            if (descs.Count > 1) throw new BridgeException("'" + path + "' exposes more than one TmcDesc");
            return (System.Xml.XmlElement)(descs.Count == 1 ? descs[0] : modules[0]);
        }

        // Every variable link whose OwnerA or OwnerB is the path or below it, as
        // "<ownerA> | <ownerB> | <varA> | <varB>" (ProduceMappingInfo is the
        // project-wide link table, independent of the tree's child counts).
        private static List<string> MappingLinksTouching(dynamic sm, string path)
        {
            string xml;
            try { xml = (string)sm.ProduceMappingInfo(); }
            catch (Exception ex) { throw new BridgeException("ProduceMappingInfo failed: " + ex.Message + " (" + ComHelpers.ErrorCode(ex) + ")"); }
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);
            var links = new List<string>();
            foreach (System.Xml.XmlElement b in doc.SelectNodes("//OwnerA/OwnerB"))
            {
                string ownerA = ((System.Xml.XmlElement)b.ParentNode).GetAttribute("Name");
                string ownerB = b.GetAttribute("Name");
                if (!OwnerUnder(ownerA, path) && !OwnerUnder(ownerB, path)) continue;
                System.Xml.XmlNodeList ls = b.SelectNodes("Link");
                if (ls.Count == 0) links.Add(ownerA + " | " + ownerB + " | (owner pair without links)");
                foreach (System.Xml.XmlElement l in ls)
                    links.Add(ownerA + " | " + ownerB + " | " + l.GetAttribute("VarA") + " | " + l.GetAttribute("VarB"));
            }
            return links;
        }

        private static bool OwnerUnder(string owner, string path)
        {
            return string.Equals(owner, path, StringComparison.OrdinalIgnoreCase) ||
                owner.StartsWith(path + "^", StringComparison.OrdinalIgnoreCase);
        }

        // Wrapper Assert-TccomReloadTargetTmc: the reviewed TMC must declare the
        // target vendor|library|version and exactly one Module with the ClassID,
        // the instance's module name, and CLSID@ClassFactory == library name.
        private static Json.JObj AssertReloadTargetTmc(string tmcPath, Guid classId, string target, string moduleName)
        {
            string[] parts = target.Split(new char[] { '|' }, 3);
            if (parts.Length != 3 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0 || parts[2].Trim().Length == 0)
                throw new BridgeException("expectedTargetClassFactoryId must be vendor|library|version: '" + target + "'");
            var doc = new System.Xml.XmlDocument();
            try { doc.Load(tmcPath); }
            catch (Exception ex) { throw new BridgeException("TMC '" + tmcPath + "' is malformed: " + ex.Message); }
            System.Xml.XmlElement root = doc.DocumentElement;
            if (root == null || root.LocalName != "TcModuleClass") throw new BridgeException("TMC '" + tmcPath + "' must have a TcModuleClass root");
            string vendor = DirectText(root, "Vendor/Name", tmcPath);
            string library = DirectText(root, "Library/Name", tmcPath);
            string version = DirectText(root, "Library/Version", tmcPath);
            if (vendor != parts[0] || library != parts[1] || version != parts[2])
                throw new BridgeException("TMC '" + tmcPath + "' declares '" + vendor + "|" + library + "|" + version + "', not target '" + target + "' (nothing changed)");
            var matches = new List<System.Xml.XmlElement>();
            foreach (System.Xml.XmlElement m in root.SelectNodes("Modules/Module"))
            {
                Guid g;
                if (Guid.TryParse(m.GetAttribute("GUID"), out g) && g == classId) matches.Add(m);
            }
            if (matches.Count != 1)
                throw new BridgeException("TMC '" + tmcPath + "' has " + matches.Count.ToString(CultureInfo.InvariantCulture) + " Module(s) with ClassID " + classId.ToString("B") + ", need exactly one");
            System.Xml.XmlElement target0 = matches[0];
            string tmcModuleName = DirectText(target0, "Name", tmcPath);
            if (tmcModuleName != moduleName)
                throw new BridgeException("TMC module name '" + tmcModuleName + "' does not match the instance's module '" + moduleName + "'");
            if (ParseGuid(DirectText(target0, "CLSID", tmcPath), "TMC CLSID") != classId)
                throw new BridgeException("TMC module CLSID does not match " + classId.ToString("B"));
            if (((System.Xml.XmlElement)target0.SelectSingleNode("CLSID")).GetAttribute("ClassFactory") != library)
                throw new BridgeException("TMC module CLSID@ClassFactory does not match Library Name '" + library + "'");
            var o = new Json.JObj();
            o["classFactoryId"] = vendor + "|" + library + "|" + version;
            o["moduleName"] = tmcModuleName;
            return o;
        }

        private static Json.JObj DiffRecords(SortedDictionary<string, string> before, SortedDictionary<string, string> after)
        {
            var added = new Json.JArr();
            var removed = new Json.JArr();
            var changed = new Json.JArr();
            foreach (var kv in before)
            {
                string v;
                if (!after.TryGetValue(kv.Key, out v)) { removed.Add(kv.Key); continue; }
                if (v == kv.Value) continue;
                var c = new Json.JObj();
                c["key"] = kv.Key;
                c["before"] = kv.Value;
                c["after"] = v;
                changed.Add(c);
            }
            foreach (var kv in after)
                if (!before.ContainsKey(kv.Key)) added.Add(kv.Key);
            var o = new Json.JObj();
            o["added"] = added;
            o["removed"] = removed;
            o["changed"] = changed;
            return o;
        }

        private static string DirectText(System.Xml.XmlElement parent, string xpath, string where)
        {
            System.Xml.XmlNodeList n = parent.SelectNodes(xpath);
            if (n.Count != 1 || n[0].InnerText.Trim().Length == 0)
                throw new BridgeException("'" + where + "' must have exactly one non-empty " + xpath);
            return n[0].InnerText.Trim();
        }

        private static string OptText(System.Xml.XmlElement parent, string xpath)
        {
            System.Xml.XmlNode n = parent.SelectSingleNode(xpath);
            return n == null ? "" : n.InnerText.Trim();
        }

        private static Guid ParseGuid(string value, string what)
        {
            Guid g;
            if (!Guid.TryParse(value, out g)) throw new BridgeException(what + " must be a GUID: '" + value + "'");
            return g;
        }

        // ---- twincat_cpp_create_project (L9118-9140) -------------------------
        private static Json.JObj CppCreateProject(ActionContext ctx)
        {
            string parentPath = "TIXC";
            string name = ctx.Payload.Str("name");
            string template = ctx.Payload.Str("template");
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("name is required");
            if (string.IsNullOrWhiteSpace(template)) throw new BridgeException("template is required");
            string before = ctx.Payload.Truthy("before") ? ctx.Payload.Str("before") : "";

            dynamic sm = ctx.SysManager();
            dynamic cpp = ComHelpers.GetTreeItem(sm, parentPath);
            dynamic child = cpp.CreateChild(name, 0, before, template);
            AssertWellFormedChild(cpp, child, name, 0, parentPath);

            ctx.Cache.Invalidate(parentPath);

            var data = new Json.JObj();
            data["parentPath"] = parentPath;
            data["child"] = ComHelpers.ConvertTreeItem(child);
            return data;
        }

        // ---- twincat_cpp_create_module (L9142-9165) --------------------------
        private static Json.JObj CppCreateModule(ActionContext ctx)
        {
            string parentPath = ctx.Payload.Str("projectPath");
            string name = ctx.Payload.Str("name");
            if (string.IsNullOrWhiteSpace(parentPath)) throw new BridgeException("projectPath is required");
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("name is required");
            PathUtil.AssertNotSafetyPath(parentPath);
            string template = (ctx.Payload.Has("template") && !string.IsNullOrWhiteSpace(ctx.Payload.Str("template")))
                ? ctx.Payload.Str("template") : "TwinCAT Class Wizard";
            string before = ctx.Payload.Truthy("before") ? ctx.Payload.Str("before") : "";

            dynamic sm = ctx.SysManager();
            dynamic proj = ComHelpers.GetTreeItem(sm, parentPath);
            dynamic child = proj.CreateChild(name, 0, before, template);
            AssertWellFormedChild(proj, child, name, 0, parentPath);

            ctx.Cache.Invalidate(parentPath);

            var data = new Json.JObj();
            data["parentPath"] = parentPath;
            data["child"] = ComHelpers.ConvertTreeItem(child);
            return data;
        }

        // ---- twincat_cpp_open (L9167-9198) -----------------------------------
        // name MUST be '' (C++ projects cannot be renamed on open), so
        // Assert-WellFormedChild is intentionally bypassed in favor of a manual
        // non-null / non-blank ghost check.
        private static Json.JObj CppOpen(ActionContext ctx)
        {
            string file = ctx.Payload.Str("file");
            if (string.IsNullOrWhiteSpace(file)) throw new BridgeException("file is required");
            if (!System.IO.File.Exists(file)) throw new BridgeException("C++ project file not found: " + file);
            int subType = (ctx.Payload.Has("subType")) ? ctx.Payload.Int("subType", 0) : 0;
            if (subType != 0 && subType != 1 && subType != 2) throw new BridgeException("subType must be 0, 1, or 2");
            string before = ctx.Payload.Truthy("before") ? ctx.Payload.Str("before") : "";

            dynamic sm = ctx.SysManager();
            dynamic cpp = ComHelpers.GetTreeItem(sm, "TIXC");
            dynamic child = cpp.CreateChild("", subType, before, file);
            if (child == null) throw new BridgeException("CreateChild returned null opening C++ project");
            string actualName = ComHelpers.SafeStr(delegate { return child.Name; });
            if (string.IsNullOrWhiteSpace(actualName))
            {
                throw new BridgeException("open produced a ghost (blank name) - check the .vcxproj/.tczip path and subType (" +
                    file + ", subType=" + subType.ToString(CultureInfo.InvariantCulture) + ")");
            }

            ctx.Cache.Invalidate("TIXC");

            var data = new Json.JObj();
            data["parentPath"] = "TIXC";
            data["file"] = file;
            data["subType"] = subType;
            data["child"] = ComHelpers.ConvertTreeItem(child);
            return data;
        }

        // ---- twincat_cpp_consume_xml (L9200-9218) ----------------------------
        // Trigger the TMC code generator on a C++ project via ConsumeXml.
        private static Json.JObj CppConsumeXml(ActionContext ctx)
        {
            string projectPath = ctx.Payload.Str("projectPath");
            if (string.IsNullOrWhiteSpace(projectPath)) throw new BridgeException("projectPath is required");
            PathUtil.AssertNotSafetyPath(projectPath);

            dynamic sm = ctx.SysManager();
            string xml = "<TreeItem><CppProjectDef><StartTmcCodeGenerator><Active>true</Active></StartTmcCodeGenerator></CppProjectDef></TreeItem>";
            SetTreeItemXmlInternal(ctx, sm, projectPath, xml);

            var data = new Json.JObj();
            data["projectPath"] = projectPath;
            data["tmcCodeGenerated"] = true;
            return data;
        }

        // ---- twincat_cpp_set_props (L9220-9252) ------------------------------
        private static Json.JObj CppSetProps(ActionContext ctx)
        {
            string projectPath = ctx.Payload.Str("projectPath");
            if (string.IsNullOrWhiteSpace(projectPath)) throw new BridgeException("projectPath is required");
            PathUtil.AssertNotSafetyPath(projectPath);

            string inner = "";
            if (ctx.Payload.Has("bootProjectEncryption") && !string.IsNullOrWhiteSpace(ctx.Payload.Str("bootProjectEncryption")))
            {
                string v = ctx.Payload.Str("bootProjectEncryption");
                if (v != "None" && v != "Target") throw new BridgeException("bootProjectEncryption must be None or Target");
                inner += "<BootProjectEncryption>" + v + "</BootProjectEncryption>";
            }
            if (ctx.Payload.Has("saveProjectSources"))
            {
                string b = ctx.Payload.Bool("saveProjectSources") ? "true" : "false";
                inner += "<TargetArchiveSettings><SaveProjectSources>" + b + "</SaveProjectSources></TargetArchiveSettings>" +
                         "<FileArchiveSettings><SaveProjectSources>" + b + "</SaveProjectSources></FileArchiveSettings>";
            }
            if (string.IsNullOrEmpty(inner))
            {
                throw new BridgeException("set_props needs at least one of bootProjectEncryption / saveProjectSources");
            }

            dynamic sm = ctx.SysManager();
            string xml = "<TreeItem><CppProjectDef>" + inner + "</CppProjectDef></TreeItem>";
            SetTreeItemXmlInternal(ctx, sm, projectPath, xml);

            var data = new Json.JObj();
            data["projectPath"] = projectPath;
            data["propsApplied"] = true;
            return data;
        }

        // ---- twincat_cpp_build_project (L9254-9304) --------------------------
        // BuildProject wants the project UniqueName, not the display name; resolve
        // it by scanning Solution.Projects. Polls via Wait-ForBuildFinish when
        // waitForFinish. Does not mutate the tree structure (no cache invalidate).
        private static Json.JObj CppBuildProject(ActionContext ctx)
        {
            string projectName = ctx.Payload.Str("projectName");
            if (string.IsNullOrWhiteSpace(projectName)) throw new BridgeException("projectName is required");
            string config = (ctx.Payload.Has("config") && !string.IsNullOrWhiteSpace(ctx.Payload.Str("config")))
                ? ctx.Payload.Str("config") : "Release|TwinCAT RT (x64)";
            bool wait = (ctx.Payload.Has("waitForFinish")) ? ctx.Payload.Bool("waitForFinish") : true;
            int timeoutMs = (ctx.Payload.Has("timeoutMs")) ? ctx.Payload.Int("timeoutMs", 1800000) : 1800000;

            dynamic dte = ctx.Dte(true);
            Json.JObj solution = GetSolutionInfo(dte);
            if (!solution.Bool("isOpen")) throw new BridgeException("No solution is open in XAE");

            string unique = null;
            dynamic projects = dte.Solution.Projects;
            int projCount = (int)projects.Count;
            for (int i = 1; i <= projCount; i++)
            {
                dynamic proj = projects.Item(i);
                if (proj == null) continue;
                dynamic p = proj;
                string pName = ComHelpers.SafeStr(delegate { return p.Name; });
                string pUnique = ComHelpers.SafeStr(delegate { return p.UniqueName; });
                if (pName == projectName || pUnique == projectName)
                {
                    unique = !string.IsNullOrWhiteSpace(pUnique) ? pUnique : pName;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(unique)) throw new BridgeException("C++ project not found in solution: " + projectName);

            dynamic solutionBuild = dte.Solution.SolutionBuild;
            solutionBuild.BuildProject(config, unique, wait);

            Json.JObj build;
            if (wait)
            {
                build = WaitForBuildFinish(solutionBuild, timeoutMs);
            }
            else
            {
                build = new Json.JObj();
                build["buildState"] = (int)solutionBuild.BuildState;
                build["lastBuildInfo"] = (object)SafeNullableInt(delegate { return solutionBuild.LastBuildInfo; });
            }

            var data = new Json.JObj();
            data["projectName"] = projectName;
            data["uniqueName"] = unique;
            data["config"] = config;
            data["waited"] = wait;
            data["build"] = build;
            return data;
        }

        // ---- twincat_cpp_publish (L9306-9328) --------------------------------
        // Confirm token (ALLOW_CPP_PUBLISH) enforced upstream in index.js; the PS
        // handler does not re-verify, so neither do we.
        private static Json.JObj CppPublish(ActionContext ctx)
        {
            string projectPath = ctx.Payload.Str("projectPath");
            if (string.IsNullOrWhiteSpace(projectPath)) throw new BridgeException("projectPath is required");
            PathUtil.AssertNotSafetyPath(projectPath);

            dynamic sm = ctx.SysManager();
            string xml = "<TreeItem><CppProjectDef><PublishModules><Active>true</Active></PublishModules></CppProjectDef></TreeItem>";
            SetTreeItemXmlInternal(ctx, sm, projectPath, xml);

            var data = new Json.JObj();
            data["projectPath"] = projectPath;
            data["published"] = true;
            data["note"] = "Modules built for all platforms and exported. Does not activate/restart the runtime.";
            return data;
        }

        // ---- shared helpers --------------------------------------------------

        // Set-TreeItemXml (L3243-3262): ConsumeXml with GetLastXmlError surfacing,
        // then invalidate the target subtree. Returns the live item.
        private static dynamic SetTreeItemXmlInternal(ActionContext ctx, dynamic sm, string targetPath, string xml)
        {
            dynamic item = ComHelpers.GetTreeItem(sm, targetPath);
            ComHelpers.ConsumeXml(item, xml);
            ctx.Cache.Invalidate(targetPath);
            return item;
        }

        // Get-SolutionInfo (L581-601): {isOpen, fullName}.
        private static Json.JObj GetSolutionInfo(dynamic dte)
        {
            dynamic solution = dte.Solution;
            string fullName = null;
            bool isOpen = false;
            bool isOpenResolved = false;

            try { fullName = (string)solution.FullName; }
            catch { }

            try { isOpen = (bool)solution.IsOpen; isOpenResolved = true; }
            catch { isOpenResolved = false; }

            if (!isOpenResolved)
            {
                isOpen = !string.IsNullOrWhiteSpace(fullName);
            }

            var o = new Json.JObj();
            o["isOpen"] = isOpen;
            o["fullName"] = fullName;
            return o;
        }

        // Wait-ForBuildFinish (L678-693): poll until BuildState != 2.
        private static Json.JObj WaitForBuildFinish(dynamic solutionBuild, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                int state = (int)solutionBuild.BuildState;
                if (state != 2)
                {
                    var done = new Json.JObj();
                    done["buildState"] = state;
                    done["lastBuildInfo"] = (int)solutionBuild.LastBuildInfo;
                    return done;
                }
                System.Threading.Thread.Sleep(500);
            }
            throw new BridgeException("Timed out waiting for build completion after " + timeoutMs + " ms");
        }

        // Get-SafeValue { [int]$x }: returns null on failure (matches PS null shape).
        private static object SafeNullableInt(Func<object> f)
        {
            try
            {
                object v = f();
                if (v == null) return null;
                return (object)ComHelpers.ToInt(v);
            }
            catch { return null; }
        }

        // Assert-WellFormedChild (L3192-3241): validate a child returned by
        // CreateChild; on a malformed "ghost" do best-effort cleanup and THROW.
        private static void AssertWellFormedChild(dynamic parent, dynamic child, string requestedName, int subType, string parentPath)
        {
            string childActualName = ComHelpers.SafeStr(delegate { return child.Name; });
            string childPath = ComHelpers.SafeStr(delegate { return child.PathName; });

            string reason = null;
            if (child == null)
            {
                reason = "CreateChild returned null";
            }
            else if (string.IsNullOrWhiteSpace(childActualName))
            {
                reason = "returned child has a blank name";
            }
            // XAE appends the module type to a TcCOM instance name:
            // "<name> (<ModuleTypeName>)" is the same child, not a ghost.
            else if (childActualName != requestedName &&
                !(childActualName.StartsWith(requestedName + " (", StringComparison.Ordinal) && childActualName.EndsWith(")", StringComparison.Ordinal)))
            {
                reason = "returned child name '" + childActualName + "' does not match requested name '" + requestedName + "'";
            }
            else
            {
                string expectedPath = parentPath + "^" + childActualName;
                if (!string.IsNullOrWhiteSpace(childPath) && childPath != expectedPath)
                {
                    reason = "returned child path '" + childPath + "' is not under requested parent (expected '" + expectedPath + "')";
                }
            }

            if (reason == null) return;

            if (!string.IsNullOrWhiteSpace(childActualName))
            {
                try { parent.DeleteChild(childActualName); }
                catch { }
            }

            throw new BridgeException("CreateChild produced a malformed child (name='" + childActualName + "', path='" + childPath +
                "') for requested name='" + requestedName + "', subType=" + subType.ToString(CultureInfo.InvariantCulture) +
                " under '" + parentPath + "' (" + reason + "). This usually means the subType/createInfo is not valid for this parent " +
                "(EtherCAT boxes typically require a proper createInfo). No usable child was created. If a stray blank-named child remains, " +
                "remove it in the XAE GUI or via close-without-save.");
        }
    }
}
