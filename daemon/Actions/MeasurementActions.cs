using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml;

namespace Te1000Daemon
{
    // Measurement (TE130X Scope-View) + TwinCAT Analytics action group.
    //
    // Scope actions drive the TE130X Scope-View Automation Interface. The scope
    // automation object exposes IMeasurementScope, which is a vtable/IUnknown
    // interface that cannot be late-bound through `dynamic` (PowerShell hit the
    // same wall — see bridge L913-922), so the PS bridge invoked it through a
    // compiled reflection shim (Te1000MeasurementHelper). That shim is pure
    // System.Reflection, so it ports 1:1 to C# here (see ScopeHelper below). If
    // the TE130X automation assembly cannot be located/loaded, EnsureScopeHelper
    // returns false and the handlers throw the SAME 'tooling not installed'
    // BridgeException the PS bridge raised.
    //
    // Analytics actions use the TIAN tree node and CreateChild/DeleteChild, the
    // same pattern as twincat_create_child/delete_child.
    //
    // C#5-clean (no interpolation, no out var, no expression-bodied members).
    internal static class MeasurementActions
    {
        public static void Register(Dictionary<string, ActionHandler> h)
        {
            h["measurement_scope_create"] = ScopeCreate;
            h["measurement_scope_add_child"] = ScopeAddChild;
            h["measurement_scope_rename"] = ScopeRename;
            h["measurement_scope_record"] = ScopeRecord;
            h["measurement_analytics_create"] = AnalyticsCreate;
            h["analytics_logger_create"] = AnalyticsLoggerCreate;
            h["analytics_stream_create"] = AnalyticsStreamCreate;
            h["analytics_logger_delete"] = AnalyticsLoggerDelete;
            h["analytics_stream_delete"] = AnalyticsStreamDelete;
            h["analytics_config_get"] = AnalyticsConfigGet;
            h["analytics_config_set"] = AnalyticsConfigSet;
        }

        // --- measurement_scope_create (L9330-9356) ---------------------------
        private static Json.JObj ScopeCreate(ActionContext ctx)
        {
            string name = ctx.Payload.Str("name");
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("name is required");

            dynamic dte = ctx.Dte(true);
            dynamic solution = dte.Solution;
            if (solution == null || !((bool)solution.IsOpen)) throw new BridgeException("No solution is open");

            string solutionFullName = (string)solution.FullName;
            string destination = ctx.Payload.Truthy("destination")
                ? ctx.Payload.Str("destination")
                : Path.GetDirectoryName(solutionFullName);

            string template = ctx.Payload.Truthy("template")
                ? ctx.Payload.Str("template")
                : GetScopeTemplatePath();
            if (string.IsNullOrWhiteSpace(template))
            {
                throw new BridgeException("Scope project template not found — TE130X Scope View tooling may not be installed. Pass template explicitly (a full .tcmproj path).");
            }
            if (!File.Exists(template)) throw new BridgeException("Scope template not found: " + template);

            dynamic proj = solution.AddFromTemplate(template, destination, name);

            ctx.Cache.Invalidate(null);

            var data = new Json.JObj();
            data["created"] = true;
            data["name"] = name;
            data["kind"] = "scope";
            data["projectFullName"] = ComHelpers.SafeStr(delegate { return proj.FullName; });
            return data;
        }

        // --- measurement_scope_add_child (L9358-9389) ------------------------
        private static Json.JObj ScopeAddChild(ActionContext ctx)
        {
            string project = ctx.Payload.Str("project");
            if (string.IsNullOrWhiteSpace(project)) throw new BridgeException("project is required");
            string name = (ctx.Payload.Has("name") && ctx.Payload.Str("name") != null) ? ctx.Payload.Str("name") : "";
            int elementType = ctx.Payload.Has("elementType") ? ctx.Payload.Int("elementType", 0) : 0;
            string parentPath = ctx.Payload.Truthy("parentPath") ? ctx.Payload.Str("parentPath") : "";

            dynamic dte = ctx.Dte(true);
            if (!EnsureScopeHelper())
            {
                throw new BridgeException("TE130X Scope automation assembly not found (TwinCAT.Measurement.AutomationInterface.dll). Scope tooling is not installed.");
            }
            object obj = GetScopeProjectObject(dte, project);
            if (!ScopeHelper.Is(obj))
            {
                throw new BridgeException("Project '" + project + "' is not a Measurement/Scope project (object is not IMeasurementScope).");
            }
            object parent = ResolveScopeElement(obj, parentPath);
            object child;
            int rc = ScopeHelper.CreateChild(parent, out child, name, elementType);

            ctx.Cache.Invalidate(null);

            var data = new Json.JObj();
            data["project"] = project;
            data["parentPath"] = parentPath;
            data["created"] = true;
            data["name"] = name;
            data["elementType"] = elementType;
            data["rc"] = rc;
            return data;
        }

        // --- measurement_scope_rename (L9391-9415) ---------------------------
        private static Json.JObj ScopeRename(ActionContext ctx)
        {
            string project = ctx.Payload.Str("project");
            string path = ctx.Payload.Str("path");
            string newName = ctx.Payload.Str("newName");
            if (string.IsNullOrWhiteSpace(project)) throw new BridgeException("project is required");
            if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("path is required");
            if (string.IsNullOrWhiteSpace(newName)) throw new BridgeException("newName is required");

            dynamic dte = ctx.Dte(true);
            if (!EnsureScopeHelper())
            {
                throw new BridgeException("TE130X Scope automation assembly not found (TwinCAT.Measurement.AutomationInterface.dll). Scope tooling is not installed.");
            }
            object obj = GetScopeProjectObject(dte, project);
            if (!ScopeHelper.Is(obj))
            {
                throw new BridgeException("Project '" + project + "' is not a Measurement/Scope project (object is not IMeasurementScope).");
            }
            object element = ResolveScopeElement(obj, path);
            int rc = ScopeHelper.ChangeName(element, newName);

            ctx.Cache.Invalidate(null);

            var data = new Json.JObj();
            data["project"] = project;
            data["path"] = path;
            data["newName"] = newName;
            data["rc"] = rc;
            return data;
        }

        // --- measurement_scope_record (L9417-9438) ---------------------------
        // NOTE: index.js guards this with ALLOW_MEASUREMENT_RECORD upstream; the
        // PS handler itself does NOT re-check a token, so neither do we.
        private static Json.JObj ScopeRecord(ActionContext ctx)
        {
            string project = ctx.Payload.Str("project");
            string state = ctx.Payload.Str("state");
            if (string.IsNullOrWhiteSpace(project)) throw new BridgeException("project is required");
            if (state != "start" && state != "stop") throw new BridgeException("state must be 'start' or 'stop'");

            dynamic dte = ctx.Dte(true);
            if (!EnsureScopeHelper())
            {
                throw new BridgeException("TE130X Scope automation assembly not found (TwinCAT.Measurement.AutomationInterface.dll). Scope tooling is not installed.");
            }
            object obj = GetScopeProjectObject(dte, project);
            if (!ScopeHelper.Is(obj))
            {
                throw new BridgeException("Project '" + project + "' is not a Measurement/Scope project (object is not IMeasurementScope).");
            }
            int rc = (state == "start") ? ScopeHelper.StartRecord(obj) : ScopeHelper.StopRecord(obj);

            var data = new Json.JObj();
            data["project"] = project;
            data["state"] = state;
            data["rc"] = rc;
            return data;
        }

        // --- measurement_analytics_create (L9440-9466) -----------------------
        private static Json.JObj AnalyticsCreate(ActionContext ctx)
        {
            string name = ctx.Payload.Str("name");
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("name is required");

            dynamic dte = ctx.Dte(true);
            dynamic solution = dte.Solution;
            if (solution == null || !((bool)solution.IsOpen)) throw new BridgeException("No solution is open");

            string solutionFullName = (string)solution.FullName;
            string destination = ctx.Payload.Truthy("destination")
                ? ctx.Payload.Str("destination")
                : Path.GetDirectoryName(solutionFullName);

            string template = ctx.Payload.Truthy("template")
                ? ctx.Payload.Str("template")
                : GetAnalyticsTemplatePath();
            if (string.IsNullOrWhiteSpace(template))
            {
                throw new BridgeException("Analytics project template not found — pass template explicitly (TwinCAT Analytics tooling may not be installed).");
            }
            if (!File.Exists(template)) throw new BridgeException("Analytics template not found: " + template);

            dynamic proj = solution.AddFromTemplate(template, destination, name);

            ctx.Cache.Invalidate(null);

            var data = new Json.JObj();
            data["created"] = true;
            data["name"] = name;
            data["kind"] = "analytics";
            data["projectFullName"] = ComHelpers.SafeStr(delegate { return proj.FullName; });
            return data;
        }

        // --- analytics_logger_create (L9468-9485) ----------------------------
        private static Json.JObj AnalyticsLoggerCreate(ActionContext ctx)
        {
            string name = ctx.Payload.Str("name");
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("name is required");
            string before = ctx.Payload.Truthy("before") ? ctx.Payload.Str("before") : "";

            dynamic sm = ctx.SysManager();
            dynamic tian = ComHelpers.GetTreeItem(sm, "TIAN");
            // Native model (TIAN ProduceXml carries AnalyticsConfig): there are no
            // DataLogger objects — item type 101 is "unknown or not usable" and
            // CreateChild(name,1) returns null (live, 2026-10-01). Refuse up front.
            if (IsNativeAnalytics(tian))
            {
                throw new BridgeException("TIAN uses the native Analytics configuration (AnalyticsConfig): DataLogger objects do not exist in this TwinCAT version, so CreateChild(name,1) returns null. " +
                    "The logger is the ActivateAlyLogger flag plus stream targets/streams: use tc_measurement analytics_set op=logger_enable / target_add / stream_add instead.");
            }
            // subType 1 = DataLogger (infosys 12562942987).
            dynamic child = tian.CreateChild(name, 1, before, null);
            AssertWellFormedChild(tian, child, name, 1, "TIAN", new string[] { name });

            ctx.Cache.Invalidate("TIAN");

            var data = new Json.JObj();
            data["parentPath"] = "TIAN";
            data["child"] = ComHelpers.ConvertTreeItem(child);
            return data;
        }

        // --- analytics_stream_create (L9487-9504) ----------------------------
        private static Json.JObj AnalyticsStreamCreate(ActionContext ctx)
        {
            string name = ctx.Payload.Str("name");
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("name is required");
            string before = ctx.Payload.Truthy("before") ? ctx.Payload.Str("before") : "";

            dynamic sm = ctx.SysManager();
            dynamic tian = ComHelpers.GetTreeItem(sm, "TIAN");
            // subType 0 = StreamHelper (infosys 12563004555). Infosys says the node is
            // '<name>_Obj1 (StreamHelper)'; live XAE names it '<name> (StreamHelper)'.
            // Accept either. This is the legacy StreamHelper object, NOT a native
            // Analytics stream (those live under a stream context: analytics_set stream_add).
            dynamic child = tian.CreateChild(name, 0, before, null);
            AssertWellFormedChild(tian, child, name, 0, "TIAN", StreamHelperNames(name));

            ctx.Cache.Invalidate("TIAN");

            var data = new Json.JObj();
            data["parentPath"] = "TIAN";
            data["child"] = ComHelpers.ConvertTreeItem(child);
            return data;
        }

        // --- analytics_logger_delete (L9506-9531) ----------------------------
        private static Json.JObj AnalyticsLoggerDelete(ActionContext ctx)
        {
            string name = ctx.Payload.Str("name");
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("name is required");
            bool dryRun = ctx.Payload.Bool("dryRun", false);

            dynamic sm = ctx.SysManager();

            if (dryRun)
            {
                dynamic tianRead = ctx.Cache.LookupItem(sm, "TIAN");
                bool exists = ChildExistsByName(tianRead, name);
                var dd = new Json.JObj();
                dd["parentPath"] = "TIAN";
                dd["name"] = name;
                dd["exists"] = exists;
                dd["deleted"] = false;
                return dd;
            }

            dynamic tian = ComHelpers.GetTreeItem(sm, "TIAN");
            tian.DeleteChild(name);
            ctx.Cache.Invalidate("TIAN");

            var data = new Json.JObj();
            data["parentPath"] = "TIAN";
            data["name"] = name;
            data["deleted"] = true;
            return data;
        }

        // --- analytics_stream_delete (L9533-9559) ----------------------------
        private static Json.JObj AnalyticsStreamDelete(ActionContext ctx)
        {
            string name = ctx.Payload.Str("name");
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("name is required");
            bool dryRun = ctx.Payload.Bool("dryRun", false);
            string[] candidates = StreamHelperNames(name);

            dynamic sm = ctx.SysManager();

            if (dryRun)
            {
                dynamic tianRead = ctx.Cache.LookupItem(sm, "TIAN");
                string found = null;
                foreach (string c in candidates) { if (ChildExistsByName(tianRead, c)) { found = c; break; } }
                var dd = new Json.JObj();
                dd["parentPath"] = "TIAN";
                dd["name"] = name;
                dd["deleteName"] = found;
                dd["candidates"] = new Json.JArr(candidates);
                dd["exists"] = found != null;
                dd["deleted"] = false;
                return dd;
            }

            // Try each known StreamHelper node name (live '<name> (StreamHelper)',
            // infosys '<name>_Obj1 (StreamHelper)'); the first DeleteChild that does
            // not throw wins.
            dynamic tian = ComHelpers.GetTreeItem(sm, "TIAN");
            string deleteName = null;
            Exception last = null;
            foreach (string c in candidates)
            {
                if (c == name) continue;
                try { tian.DeleteChild(c); deleteName = c; break; }
                catch (Exception ex) { last = ex; }
            }
            if (deleteName == null)
            {
                throw new BridgeException("No StreamHelper node found for '" + name + "' (tried '" + candidates[0] + "', '" + candidates[1] + "'): " + (last == null ? "" : last.Message));
            }
            ctx.Cache.Invalidate("TIAN");

            var data = new Json.JObj();
            data["parentPath"] = "TIAN";
            data["name"] = name;
            data["deleteName"] = deleteName;
            data["deleted"] = true;
            return data;
        }

        // ===================================================================
        // Native Analytics configuration: analytics_config_get / analytics_config_set
        // ===================================================================
        // Live-probed read-only (TcXaeShell 17, TCatSysManagerLib TREEITEMTYPE_
        // ANALYTICSCONFIG=100 / LOGGER=101 / STREAM=102 / STREAMCONTEXT=103):
        //  - TIAN (100) ProduceXml: TreeItem > AdiOids/AdiOid* (OIDs of every
        //    ADI-capable object, INCLUDING each stream), AnalyticsConfig > Config
        //    {StreamTargets/StreamTargetItem[Id], ActivateAlyLogger}, AnalyticsConfig >
        //    StreamContexts/StreamContext[AdiOid,CallerOid,Category,Hide]{ItemName}.
        //    ChildCount 0; contexts/streams are invisible to LookupTreeItem and to
        //    child enumeration.
        //  - sysManager.LookupTreeItemById(0, streamOid) resolves a stream (ItemType
        //    102, path 'TIAN^<context ItemName>^<stream>'); its ProduceXml holds
        //    AnalyticsStream[AdiOid,CallerOid,Category,Oid] > Config. stream.Parent is
        //    the context item (ItemType 103). Typed lookups with 101/102/103 throw.
        //  - Hide is the Stream Sources checkbox (Hide=false = source selected).
        //    Consuming TIAN XML that LISTS a StreamContext selected it (Hide->false,
        //    and Hide=true in that same XML did not stick). So TIAN writes never echo
        //    the produced StreamContexts by default (contextsMode 'omit').
        //  - TIAN/stream XML is only a PROJECTION of TF3500's managed model
        //    (TwinCAT.Analytics.Logger.SystemManagerExtension 4.4.93, read by
        //    reflection): ConfigModel.ReCalc reads the TIAN XML back through
        //    RestoreProperties -> ImportStreamTargets (add/update by Id, NEVER
        //    removes) and RestoreStreamSources (StreamContexts/Hide), then
        //    SaveProperties -> SendConfigToTreeItem republishes the WHOLE model.
        //    StreamModel.ReCalc never reads Config back; it only republishes. So a
        //    target removed from the XML returns on the next model republish, and a
        //    stream Config written to the stream item is overwritten. The only
        //    importer of stream Config (ConfigModel.Import via the ImportConfiguration
        //    command) opens a file dialog. target_remove and stream_edit therefore do
        //    not go through ConsumeXml: they edit the saved .tsproj on disk while the
        //    project is unloaded (or the solution closed) and TF3500 rebuilds its
        //    model from the file on load (OfflineTsproj; proven live 2026-10-01).
        // Every write is read-modify-write of the FULL produced XML inside the
        // daemon (a partial TIAN ConsumeXml replaced the whole Config and wiped all
        // targets). Credentials never leave the daemon: MQTT settings are redacted
        // to a whitelist on every output. Verified live: logger_enable, target_add,
        // target_edit, context_hide. stream_add, stream_remove and the offline
        // target_remove/stream_edit paths are not.

        private const string RedactedValue = "<redacted>";
        private static readonly string[] MqttSafeLeaves = new string[] {
            "BrokerPort", "WithCertificates", "CommunicationTimeout", "KeepAlivePeriod", "TcpBufferSize",
            "SecurityType", "TlsVersion", "Insecure", "IgnoreExpiration", "IsPskIdCaseSensitive", "IgnoreCnMismatch", "PskMode"
        };
        private static readonly System.Text.RegularExpressions.Regex SecretName =
            new System.Text.RegularExpressions.Regex("crypt|passw|psk|cert|secret|token|key", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly string[] StreamKeyFields = new string[] {
            "AutostartStream", "SamplingDivider", "SamplesPerBuffer", "TargetId", "BuffersPerFile", "UseRingbuffer",
            "FilesPerRingbuffer", "QueueMessages", "MqttBuffersPerQueue", "MqttQueueInFile", "MainTopic", "CompressionMode"
        };

        private sealed class AlyStream
        {
            public string Oid;
            public dynamic Item;
            public XmlDocument Doc;
            public string Name;
            public string Path;
            public string CallerOid;
        }

        private sealed class AlySnapshot
        {
            public dynamic Tian;
            public XmlDocument TianDoc;
            public List<AlyStream> Streams;
        }

        private static bool IsNativeAnalytics(dynamic tian)
        {
            string xml = ComHelpers.SafeStr(delegate { return tian.ProduceXml(); });
            return xml != null && xml.IndexOf("<AnalyticsConfig", StringComparison.Ordinal) >= 0;
        }

        private static XmlDocument LoadXml(string xml)
        {
            var d = new XmlDocument();
            d.XmlResolver = null;
            d.PreserveWhitespace = true;
            d.LoadXml(xml);
            return d;
        }

        private static XmlDocument CloneDoc(XmlDocument d) { return LoadXml(d.OuterXml); }

        // '#x08502000' / '0x08502000' / '139468800' -> '0x08502000'.
        private static string NormOid(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            uint v;
            if (s.StartsWith("#x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return s;
            }
            else
            {
                long l;
                if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return s;
                v = unchecked((uint)l);
            }
            return "0x" + v.ToString("x8", CultureInfo.InvariantCulture);
        }

        private static string NormGuid(string s)
        {
            if (s == null) return null;
            Guid g;
            return Guid.TryParse(s.Trim(), out g) ? g.ToString("D") : s.Trim();
        }

        private static string Text(XmlNode parent, string xpath)
        {
            if (parent == null) return null;
            XmlNode n = parent.SelectSingleNode(xpath);
            return n == null ? null : n.InnerText;
        }

        private static bool HasElementChild(XmlNode e)
        {
            foreach (XmlNode c in e.ChildNodes) if (c.NodeType == XmlNodeType.Element) return true;
            return false;
        }

        private static List<XmlElement> Elements(XmlNode root, string xpath)
        {
            var l = new List<XmlElement>();
            foreach (XmlNode n in root.SelectNodes(xpath)) { XmlElement e = n as XmlElement; if (e != null) l.Add(e); }
            return l;
        }

        private static List<XmlElement> Targets(XmlDocument tian) { return Elements(tian, "/TreeItem/AnalyticsConfig/Config/StreamTargets/StreamTargetItem"); }
        private static List<XmlElement> Contexts(XmlDocument tian) { return Elements(tian, "/TreeItem/AnalyticsConfig/StreamContexts/StreamContext"); }
        private static XmlElement StreamConfig(XmlDocument s) { return s.SelectSingleNode("/TreeItem/AnalyticsStream/Config") as XmlElement; }

        private static AlySnapshot ReadAnalytics(dynamic sm)
        {
            var s = new AlySnapshot();
            s.Tian = ComHelpers.GetTreeItem(sm, "TIAN");
            string xml = ComHelpers.ProduceXml(s.Tian);
            s.TianDoc = LoadXml(xml);
            if (s.TianDoc.SelectSingleNode("/TreeItem/AnalyticsConfig/Config") == null)
            {
                throw new BridgeException("TIAN has no native AnalyticsConfig/Config (Analytics not configured, or the legacy DataLogger model); analytics_get/analytics_set support only the native model.");
            }
            s.Streams = new List<AlyStream>();
            foreach (XmlElement n in Elements(s.TianDoc, "/TreeItem/AdiOids/AdiOid"))
            {
                string oid = NormOid(n.InnerText);
                if (oid == null || !oid.StartsWith("0x", StringComparison.Ordinal)) continue;
                int id = unchecked((int)uint.Parse(oid.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                dynamic item = null;
                try { item = sm.LookupTreeItemById(0, id); } catch { item = null; }
                if (item == null) continue;
                if (ComHelpers.SafeInt(delegate { return item.ItemType; }, -1) != 102) continue;
                AddStream(s, item, oid);
            }
            // AdiOids only lists streams known when TIAN was last recalculated; a stream created in
            // the GUI since then is missing there. Selected sources are tree children of TIAN
            // (context, ItemType 103) with their streams (ItemType 102) below them.
            foreach (dynamic context in ComHelpers.Children(s.Tian))
            {
                if (ComHelpers.SafeInt(delegate { return context.ItemType; }, -1) != 103) continue;
                foreach (dynamic item in ComHelpers.Children(context))
                {
                    if (ComHelpers.SafeInt(delegate { return item.ItemType; }, -1) != 102) continue;
                    AddStream(s, item, null);
                }
            }
            return s;
        }

        // Adds the stream unless an entry with the same OID is already in the snapshot. The OID is the
        // stream's own AnalyticsStream/@Oid, or knownOid when the caller already resolved it by id.
        private static void AddStream(AlySnapshot s, dynamic item, string knownOid)
        {
            var st = new AlyStream();
            st.Item = item;
            string sx = (string)ComHelpers.ProduceXml(item);
            st.Doc = LoadXml(sx);
            XmlElement aly = st.Doc.SelectSingleNode("/TreeItem/AnalyticsStream") as XmlElement;
            string own = aly == null ? null : NormOid(aly.GetAttribute("Oid"));
            st.Oid = string.IsNullOrEmpty(own) ? knownOid : own;
            if (string.IsNullOrEmpty(st.Oid)) return;
            foreach (AlyStream known in s.Streams) if (known.Oid == st.Oid) return;
            st.Name = ComHelpers.SafeStr(delegate { return item.Name; });
            st.Path = ComHelpers.SafeStr(delegate { return item.PathName; });
            st.CallerOid = aly == null ? null : NormOid(aly.GetAttribute("CallerOid"));
            s.Streams.Add(st);
        }

        // Flatten leaf elements under e into key -> {raw, display}. Inside
        // MqttConnectionSettings only whitelisted leaves are displayed; any leaf
        // whose name looks secret is redacted everywhere.
        private static void FlattenLeaves(XmlElement e, string prefix, bool inMqtt, Dictionary<string, string[]> into, string skipChild)
        {
            var counts = new Dictionary<string, int>();
            foreach (XmlNode c in e.ChildNodes)
            {
                XmlElement ce = c as XmlElement;
                if (ce == null) continue;
                int k; counts.TryGetValue(ce.Name, out k); counts[ce.Name] = k + 1;
            }
            var seen = new Dictionary<string, int>();
            foreach (XmlNode c in e.ChildNodes)
            {
                XmlElement ce = c as XmlElement;
                if (ce == null || ce.Name == skipChild) continue;
                string key = ce.Name;
                if (counts[ce.Name] > 1)
                {
                    int i; seen.TryGetValue(ce.Name, out i); seen[ce.Name] = i + 1;
                    key = key + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                }
                string path = prefix.Length == 0 ? key : prefix + "." + key;
                bool mqtt = inMqtt || ce.Name == "MqttConnectionSettings";
                if (HasElementChild(ce)) { FlattenLeaves(ce, path, mqtt, into, null); continue; }
                string raw = ce.InnerText;
                bool secret = Array.IndexOf(MqttSafeLeaves, ce.Name) < 0 && (mqtt || SecretName.IsMatch(ce.Name));
                into[path] = new string[] { raw, secret && raw.Length > 0 ? RedactedValue : raw };
            }
        }

        private static List<string> StreamSymbols(XmlElement cfg)
        {
            var l = new List<string>();
            XmlNode sn = cfg == null ? null : cfg.SelectSingleNode("SymbolNames");
            if (sn == null) return l;
            // TF3500 stores the selection as base64 of the NUL-separated symbol names (trailing NUL).
            if (!HasElementChild(sn))
            {
                string b64 = sn.InnerText.Trim();
                if (b64.Length == 0) return l;
                byte[] raw;
                try { raw = Convert.FromBase64String(b64); }
                catch (FormatException) { l.Add(b64); return l; }
                foreach (string n in System.Text.Encoding.UTF8.GetString(raw).Split('\0'))
                {
                    if (n.Length > 0) l.Add(n);
                }
                return l;
            }
            foreach (XmlNode c in sn.ChildNodes)
            {
                XmlElement ce = c as XmlElement;
                if (ce == null) continue;
                string n = ce.GetAttribute("Name");
                if (string.IsNullOrEmpty(n)) n = ce.GetAttribute("name");
                if (string.IsNullOrEmpty(n)) n = ce.InnerText.Trim();
                l.Add(n);
            }
            return l;
        }

        private static void FlattenTian(XmlDocument tian, Dictionary<string, string[]> into)
        {
            string v = Text(tian, "/TreeItem/AnalyticsConfig/Config/ActivateAlyLogger");
            into["logger.ActivateAlyLogger"] = new string[] { v, v };
            foreach (XmlElement t in Targets(tian))
            {
                FlattenLeaves(t, "target[" + NormGuid(t.GetAttribute("Id")) + "]", false, into, null);
            }
            foreach (XmlElement c in Contexts(tian))
            {
                string p = "context[" + NormOid(c.GetAttribute("CallerOid")) + "].";
                string name = Text(c, "ItemName");
                into[p + "ItemName"] = new string[] { name, name };
                into[p + "AdiOid"] = new string[] { NormOid(c.GetAttribute("AdiOid")), NormOid(c.GetAttribute("AdiOid")) };
                into[p + "Category"] = new string[] { c.GetAttribute("Category"), c.GetAttribute("Category") };
                into[p + "Hide"] = new string[] { c.GetAttribute("Hide"), c.GetAttribute("Hide") };
            }
        }

        private static void FlattenStream(string oid, string name, XmlDocument doc, Dictionary<string, string[]> into)
        {
            string p = "stream[" + oid + "]";
            into[p + ".Name"] = new string[] { name, name };
            XmlElement aly = doc.SelectSingleNode("/TreeItem/AnalyticsStream") as XmlElement;
            string caller = aly == null ? null : NormOid(aly.GetAttribute("CallerOid"));
            into[p + ".CallerOid"] = new string[] { caller, caller };
            XmlElement cfg = StreamConfig(doc);
            if (cfg == null) return;
            FlattenLeaves(cfg, p, false, into, "SymbolNames");
            string syms = string.Join(",", StreamSymbols(cfg).ToArray());
            into[p + ".SymbolNames"] = new string[] { syms, syms };
        }

        private static Dictionary<string, string[]> FlattenAll(XmlDocument tian, List<AlyStream> streams)
        {
            var d = new Dictionary<string, string[]>();
            FlattenTian(tian, d);
            foreach (AlyStream st in streams)
            {
                FlattenStream(st.Oid, st.Name, st.Doc, d);
            }
            return d;
        }

        private static Json.JArr Diff(Dictionary<string, string[]> a, Dictionary<string, string[]> b)
        {
            var keys = new List<string>(a.Keys);
            foreach (string k in b.Keys) if (!a.ContainsKey(k)) keys.Add(k);
            var ea = Entities(a.Keys);
            var eb = Entities(b.Keys);
            var collapsed = new HashSet<string>();
            var arr = new Json.JArr();
            foreach (string k in keys)
            {
                // A whole target/context/stream added or removed: one entry, not one per leaf.
                string ent = Entity(k);
                if (ent != null && ea.Contains(ent) != eb.Contains(ent))
                {
                    if (!collapsed.Add(ent)) continue;
                    var e = new Json.JObj();
                    e["key"] = ent;
                    e["before"] = ea.Contains(ent) ? "present" : null;
                    e["after"] = eb.Contains(ent) ? "present" : null;
                    arr.Add(e);
                    continue;
                }
                string[] x; string[] y;
                bool hx = a.TryGetValue(k, out x);
                bool hy = b.TryGetValue(k, out y);
                if (hx && hy && x[0] == y[0]) continue;
                var o = new Json.JObj();
                o["key"] = k;
                o["before"] = hx ? x[1] : null;
                o["after"] = hy ? y[1] : null;
                if ((hx && x[1] == RedactedValue) || (hy && y[1] == RedactedValue)) o["secretChanged"] = true;
                arr.Add(o);
            }
            return arr;
        }

        // 'target[<id>].Name' -> 'target[<id>]'; keys without an entity -> null.
        private static string Entity(string key)
        {
            int i = key.IndexOf(']');
            return (i > 0 && key.IndexOf('[') < i && key.IndexOf('.') > i) ? key.Substring(0, i + 1) : null;
        }

        private static HashSet<string> Entities(IEnumerable<string> keys)
        {
            var h = new HashSet<string>();
            foreach (string k in keys) { string e = Entity(k); if (e != null) h.Add(e); }
            return h;
        }

        private static Json.JObj LeafObj(XmlElement e, string skipChild)
        {
            var d = new Dictionary<string, string[]>();
            FlattenLeaves(e, "", false, d, skipChild);
            var o = new Json.JObj();
            foreach (var kv in d) o[kv.Key] = kv.Value[1];
            return o;
        }

        private static Json.JObj StreamModel(AlyStream st, bool verbose)
        {
            var o = new Json.JObj();
            o["streamOid"] = st.Oid;
            o["name"] = st.Name;
            o["path"] = st.Path;
            XmlElement aly = st.Doc.SelectSingleNode("/TreeItem/AnalyticsStream") as XmlElement;
            string eb = aly == null ? "" : aly.GetAttribute("eventBased");
            o["eventBased"] = eb.Length == 0 ? null : eb;
            XmlElement cfg = StreamConfig(st.Doc);
            if (cfg != null)
            {
                Json.JObj all = LeafObj(cfg, "SymbolNames");
                if (!verbose)
                {
                    var key = new Json.JObj();
                    foreach (string k in StreamKeyFields) if (all.Has(k)) key[k] = all[k];
                    all = key;
                }
                o["config"] = all;
                List<string> syms = StreamSymbols(cfg);
                o["symbolCount"] = syms.Count;
                o["symbols"] = new Json.JArr(syms.Take(200).Cast<object>());
            }
            return o;
        }

        private static string StreamTargetId(AlyStream st)
        {
            return NormGuid(Text(StreamConfig(st.Doc), "TargetId"));
        }

        // --- analytics_config_get --------------------------------------------
        private static Json.JObj AnalyticsConfigGet(ActionContext ctx)
        {
            dynamic sm = ctx.SysManager();
            AlySnapshot s = ReadAnalytics(sm);
            bool verbose = ctx.Payload.Bool("verbose", false);
            var m = new Json.JObj();
            var flat = FlattenAll(s.TianDoc, s.Streams);
            Json.JArr drift = Drift(flat);
            if (drift != null) m["driftSinceLastCall"] = drift;
            _lastSeen = flat;
            m["activateAlyLogger"] = Text(s.TianDoc, "/TreeItem/AnalyticsConfig/Config/ActivateAlyLogger");

            var targets = new Json.JArr();
            foreach (XmlElement t in Targets(s.TianDoc))
            {
                string id = NormGuid(t.GetAttribute("Id"));
                var o = new Json.JObj();
                o["targetId"] = id;
                // FILE targets carry an unused MqttConnectionSettings block; keep it out of the summary.
                o["fields"] = LeafObj(t, Text(t, "Type") == "FILE" ? "MqttConnectionSettings" : null);
                var refs = new Json.JArr();
                foreach (AlyStream st in s.Streams) if (StreamTargetId(st) == id) refs.Add(st.Oid);
                o["referencedByStreams"] = refs;
                targets.Add(o);
            }
            m["targets"] = targets;

            var matched = new HashSet<string>();
            var contexts = new Json.JArr();
            foreach (XmlElement c in Contexts(s.TianDoc))
            {
                string caller = NormOid(c.GetAttribute("CallerOid"));
                var o = new Json.JObj();
                o["callerOid"] = caller;
                o["adiOid"] = NormOid(c.GetAttribute("AdiOid"));
                o["category"] = c.GetAttribute("Category");
                o["itemName"] = Text(c, "ItemName");
                o["hide"] = c.GetAttribute("Hide");
                var streams = new Json.JArr();
                foreach (AlyStream st in s.Streams)
                {
                    if (st.CallerOid != caller) continue;
                    streams.Add(StreamModel(st, verbose));
                    matched.Add(st.Oid);
                }
                o["streams"] = streams;
                contexts.Add(o);
            }
            m["contexts"] = contexts;
            var orphans = new Json.JArr();
            foreach (AlyStream st in s.Streams) if (!matched.Contains(st.Oid)) orphans.Add(StreamModel(st, verbose));
            m["orphanStreams"] = orphans;
            m["streamDiscovery"] = "TIAN AdiOids -> LookupTreeItemById(0, oid), plus TIAN context children (ItemType 103) -> streams (ItemType 102)";
            return m;
        }

        // Format a JSON payload value as XML text (bool -> true/false, integral double without '.0').
        private static string XmlValue(object v)
        {
            if (v == null) return "";
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is double)
            {
                double d = (double)v;
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
                return d.ToString("R", CultureInfo.InvariantCulture);
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        // Apply {"A": v, "B.C": v, "D": {"E": v}} onto existing LEAF elements under
        // root. Unknown/non-leaf paths and *Crypted leaves are refused (the crypted
        // values are produced by XAE's Broker Connect dialog; clone them via copyFrom).
        private static void ApplyLeaves(XmlElement root, Json.JObj values, string prefix)
        {
            if (values == null) return;
            foreach (var kv in values)
            {
                string[] parts = kv.Key.Split('.');
                XmlElement cur = root;
                foreach (string p in parts)
                {
                    XmlElement next = cur.SelectSingleNode(p) as XmlElement;
                    if (next == null) throw new BridgeException("Unknown field '" + prefix + kv.Key + "' (no <" + p + "> element in the produced XML).");
                    cur = next;
                }
                Json.JObj nested = kv.Value as Json.JObj;
                if (nested != null) { ApplyLeaves(cur, nested, prefix + kv.Key + "."); continue; }
                if (HasElementChild(cur)) throw new BridgeException("Field '" + prefix + kv.Key + "' is not a leaf; address its children instead.");
                if (cur.Name.EndsWith("Crypted", StringComparison.Ordinal))
                {
                    throw new BridgeException("'" + prefix + kv.Key + "' is an XAE-encrypted value and cannot be set; use target_add copyFrom=<targetId> to clone existing credentials.");
                }
                cur.InnerText = XmlValue(kv.Value);
            }
        }

        private static XmlElement FindTarget(XmlDocument tian, string id)
        {
            foreach (XmlElement t in Targets(tian)) if (NormGuid(t.GetAttribute("Id")) == id) return t;
            throw new BridgeException("Stream target not found: " + id);
        }

        private static XmlElement FindContext(XmlDocument tian, string callerOid)
        {
            XmlElement hit = null;
            foreach (XmlElement c in Contexts(tian))
            {
                if (NormOid(c.GetAttribute("CallerOid")) != callerOid) continue;
                if (hit != null) throw new BridgeException("More than one StreamContext has CallerOid " + callerOid);
                hit = c;
            }
            if (hit == null) throw new BridgeException("StreamContext not found for callerOid " + callerOid);
            return hit;
        }

        private static AlyStream FindStream(AlySnapshot s, Json.JObj p)
        {
            string oid = p.Truthy("streamOid") ? NormOid(p.Str("streamOid")) : null;
            string name = p.Truthy("stream") ? p.Str("stream") : null;
            if (oid == null && name == null) throw new BridgeException("streamOid or stream (name) is required");
            AlyStream hit = null;
            foreach (AlyStream st in s.Streams)
            {
                if (oid != null ? st.Oid != oid : st.Name != name) continue;
                if (hit != null) throw new BridgeException("Stream name '" + name + "' is ambiguous; pass streamOid.");
                hit = st;
            }
            if (hit == null) throw new BridgeException("Stream not found: " + (oid ?? name));
            return hit;
        }

        // TIAN ConsumeXml payload: the full planned document with StreamContexts
        // handled per mode — 'omit' (default: drop the element, never re-select
        // sources), 'visibleOnly' (keep only Hide=false contexts), 'asProduced'
        // (echo everything; known to select every listed source).
        private static string TianPayload(XmlDocument planned, string mode)
        {
            XmlDocument d = CloneDoc(planned);
            XmlNode scs = d.SelectSingleNode("/TreeItem/AnalyticsConfig/StreamContexts");
            if (scs != null && mode == "omit")
            {
                scs.ParentNode.RemoveChild(scs);
            }
            else if (scs != null && mode == "visibleOnly")
            {
                foreach (XmlElement c in Contexts(d))
                {
                    if (c.GetAttribute("Hide").Trim() != "false") c.ParentNode.RemoveChild(c);
                }
            }
            return d.OuterXml;
        }

        // --- analytics_config_set ----------------------------------------------
        private static Json.JObj AnalyticsConfigSet(ActionContext ctx)
        {
            Json.JObj p = ctx.Payload;
            string op = ctx.Require("op");
            bool dryRun = p.Bool("dryRun", false);
            string mode = p.Truthy("contextsMode") ? p.Str("contextsMode") : "omit";
            if (mode != "omit" && mode != "visibleOnly" && mode != "asProduced") throw new BridgeException("contextsMode must be omit|visibleOnly|asProduced");

            dynamic sm = ctx.SysManager();
            AlySnapshot before = ReadAnalytics(sm);
            var result = new Json.JObj();
            result["op"] = op;
            result["dryRun"] = dryRun;
            Json.JArr drift = Drift(FlattenAll(before.TianDoc, before.Streams));
            if (drift != null) result["driftSinceLastCall"] = drift;
            if (drift != null && !dryRun && !p.Bool("acceptDrift", false))
            {
                throw new BridgeException("Analytics configuration changed since this daemon last read it (" + drift.Count.ToString(CultureInfo.InvariantCulture) +
                    " keys, e.g. '" + ((Json.JObj)drift[0]).Str("key") + "'): XAE's Analytics model may have republished an older state. Inspect with analytics_get, then re-run with acceptDrift:true.");
            }

            if (op == "stream_add") return StreamAdd(ctx, sm, before, result);
            if (op == "stream_remove") return StreamRemove(ctx, sm, before, result);
            if (op == "target_remove") return TargetRemoveOffline(ctx, before, result);
            if (op == "stream_edit") return StreamEditOffline(ctx, before, result);

            XmlDocument planned = null;       // TIAN plan
            switch (op)
            {
                case "logger_enable":
                {
                    if (!p.Has("enabled")) throw new BridgeException("enabled is required");
                    planned = CloneDoc(before.TianDoc);
                    XmlNode n = planned.SelectSingleNode("/TreeItem/AnalyticsConfig/Config/ActivateAlyLogger");
                    if (n == null) throw new BridgeException("Produced TIAN XML has no ActivateAlyLogger element.");
                    n.InnerText = p.Bool("enabled") ? "true" : "false";
                    break;
                }
                case "target_edit":
                {
                    planned = CloneDoc(before.TianDoc);
                    XmlElement t = FindTarget(planned, NormGuid(ctx.Require("targetId")));
                    Json.JObj fields = p.Obj("fields");
                    if (fields == null || fields.Count == 0) throw new BridgeException("fields is required");
                    ApplyLeaves(t, fields, "");
                    break;
                }
                case "target_add":
                {
                    planned = CloneDoc(before.TianDoc);
                    Json.JObj fields = p.Obj("fields");
                    if (fields == null || !fields.Truthy("Name")) throw new BridgeException("fields.Name is required");
                    List<XmlElement> all = Targets(planned);
                    XmlElement template = null;
                    bool copy = p.Truthy("copyFrom");
                    if (copy) template = FindTarget(planned, NormGuid(p.Str("copyFrom")));
                    else
                    {
                        string type = fields.Truthy("Type") ? fields.Str("Type") : "FILE";
                        foreach (XmlElement t in all) if (Text(t, "Type") == type) { template = t; break; }
                    }
                    if (template == null) throw new BridgeException("No existing stream target to use as the element template; pass copyFrom or add one target in XAE first.");
                    XmlElement clone = (XmlElement)template.CloneNode(true);
                    if (!copy)
                    {
                        // Never inherit another target's credentials/broker implicitly.
                        foreach (XmlElement leaf in Elements(clone, ".//MqttConnectionSettings//*"))
                        {
                            if (!HasElementChild(leaf) && Array.IndexOf(MqttSafeLeaves, leaf.Name) < 0) leaf.InnerText = "";
                        }
                    }
                    string newId = Guid.NewGuid().ToString("D");
                    clone.SetAttribute("Id", newId);
                    template.ParentNode.AppendChild(clone);
                    ApplyLeaves(clone, fields, "");
                    result["newTargetId"] = newId;
                    break;
                }
                case "context_hide":
                {
                    if (!p.Has("hide")) throw new BridgeException("hide is required");
                    planned = CloneDoc(before.TianDoc);
                    XmlElement c = FindContext(planned, NormOid(ctx.Require("callerOid")));
                    c.SetAttribute("Hide", p.Bool("hide") ? "true" : "false");
                    mode = "visibleOnly"; // the listed set IS the selected-source set
                    break;
                }
                default:
                    throw new BridgeException("Unknown op '" + op + "'. Expected logger_enable|target_add|target_edit|target_remove|context_hide|stream_add|stream_edit|stream_remove.");
            }

            var beforeFlat = FlattenAll(before.TianDoc, before.Streams);
            var plannedFlat = FlattenAll(planned, before.Streams);
            result["contextsMode"] = planned != null ? mode : null;
            result["plannedDiff"] = Diff(beforeFlat, plannedFlat);
            if (dryRun) { result["written"] = false; return result; }

            ComHelpers.ConsumeXml(before.Tian, TianPayload(planned, mode));
            ctx.Cache.Invalidate("TIAN");
            return Finish(sm, p, beforeFlat, plannedFlat, result);
        }

        // Last full state this daemon saw (after a get, or the settled state after
        // a write). Every call compares against it, so a model republish between
        // calls (the resurrected-target case) is reported instead of silently
        // becoming the next write's baseline.
        // ponytail: one slot per daemon process, keyed by nothing; a second open
        // solution would show up as drift, which is the safe direction.
        private static Dictionary<string, string[]> _lastSeen;

        private static Json.JArr Drift(Dictionary<string, string[]> now)
        {
            Json.JArr d = _lastSeen == null ? null : Diff(_lastSeen, now);
            return (d == null || d.Count == 0) ? null : d;
        }

        // Re-read the FULL state twice: right after the write and again after
        // settleMs (default 2000), because the TF3500 model republishes on its own
        // recalc. Diff/notAsPlanned use the settled read and cover every key of
        // every target, context and stream; 'unstable' lists keys that moved
        // between the two reads.
        private static Json.JObj Finish(dynamic sm, Json.JObj p, Dictionary<string, string[]> beforeFlat, Dictionary<string, string[]> plannedFlat, Json.JObj result)
        {
            AlySnapshot first = ReadAnalytics(sm);
            var firstFlat = FlattenAll(first.TianDoc, first.Streams);
            int settle = Math.Max(0, Math.Min(10000, p.Int("settleMs", 2000)));
            System.Threading.Thread.Sleep(settle);
            AlySnapshot after = ReadAnalytics(sm);
            var afterFlat = FlattenAll(after.TianDoc, after.Streams);
            _lastSeen = afterFlat;
            result["written"] = true;
            result["settleMs"] = settle;
            result["diff"] = Diff(beforeFlat, afterFlat);
            Json.JArr unstable = Diff(firstFlat, afterFlat);
            if (unstable.Count > 0) result["unstable"] = unstable;
            if (plannedFlat != null)
            {
                Json.JArr off = Diff(plannedFlat, afterFlat);
                result["notAsPlanned"] = off;
                result["verified"] = off.Count == 0 && unstable.Count == 0;
            }
            return result;
        }

        // Context item: a stream's Parent (works while the context is hidden), else
        // LookupTreeItem('TIAN^<ItemName>') when the name is unique (needs Hide=false).
        private static dynamic ResolveContextItem(dynamic sm, AlySnapshot s, string callerOid)
        {
            foreach (AlyStream st in s.Streams)
            {
                if (st.CallerOid != callerOid) continue;
                dynamic parent = null;
                try { parent = st.Item.Parent; } catch { parent = null; }
                if (parent != null) return parent;
            }
            XmlElement c = FindContext(s.TianDoc, callerOid);
            string name = Text(c, "ItemName");
            int same = 0;
            foreach (XmlElement o in Contexts(s.TianDoc)) if (Text(o, "ItemName") == name) same++;
            dynamic item = same == 1 ? ComHelpers.TryGetTreeItem(sm, "TIAN^" + name) : null;
            if (item == null)
            {
                throw new BridgeException("StreamContext " + callerOid + " ('" + name + "') is not addressable: it has no stream to reach it through" +
                    (same > 1 ? " and its ItemName is shared by " + same.ToString(CultureInfo.InvariantCulture) + " contexts" : " and is hidden (Hide=" + c.GetAttribute("Hide") + ")") +
                    ". Select the source first (op=context_hide hide=false), or add the first stream in XAE.");
            }
            return item;
        }

        // stream_add (EXPERIMENTAL, unverified live): CreateChild(name, subType=0)
        // on the context item, ghost-guarded (must come back ItemType 102 named
        // <name>). TF3500 itself creates streams by ConsumeXml of
        // <AddStream Name Oid IsEventBased/> on the context item
        // (StreamContextModel.CreateStreamProgrammatically) - the fallback if
        // CreateChild proves unusable.
        private static Json.JObj StreamAdd(ActionContext ctx, dynamic sm, AlySnapshot before, Json.JObj result)
        {
            Json.JObj p = ctx.Payload;
            string callerOid = NormOid(ctx.Require("callerOid"));
            string name = ctx.Require("name");
            int subType = p.Int("subType", 0);
            foreach (AlyStream st in before.Streams)
            {
                if (st.CallerOid == callerOid && st.Name == name) throw new BridgeException("Context " + callerOid + " already has a stream named '" + name + "' (" + st.Oid + ").");
            }
            dynamic ctxItem = ResolveContextItem(sm, before, callerOid);
            string ctxPath = ComHelpers.SafeStr(delegate { return ctxItem.PathName; });
            result["contextPath"] = ctxPath;
            result["name"] = name;
            result["subType"] = subType;
            if (IsDryRun(p)) { result["written"] = false; return result; }

            var beforeFlat = FlattenAll(before.TianDoc, before.Streams);
            dynamic child = ctxItem.CreateChild(name, subType, "", null);
            string actual = child == null ? null : ComHelpers.SafeStr(delegate { return child.Name; });
            int type = child == null ? -1 : ComHelpers.SafeInt(delegate { return child.ItemType; }, -1);
            if (child == null || actual != name || type != 102)
            {
                if (!string.IsNullOrWhiteSpace(actual)) { try { ctxItem.DeleteChild(actual); } catch { } }
                throw new BridgeException("CreateChild('" + name + "', " + subType.ToString(CultureInfo.InvariantCulture) + ") under '" + ctxPath + "' did not produce an Analytics stream (got " +
                    (child == null ? "null" : "name='" + actual + "', itemType=" + type.ToString(CultureInfo.InvariantCulture)) + "); any stray child was deleted. Add the stream in XAE (Stream Sources tab) instead.");
            }
            result["created"] = ComHelpers.ConvertTreeItem(child);
            ctx.Cache.Invalidate("TIAN");
            Json.JObj r = Finish(sm, p, beforeFlat, null, result);
            // Verified = a stream OID absent before is present, under this
            // context and with this name, in the post-settle read (_lastSeen).
            string newOid = null;
            foreach (var kv in _lastSeen)
            {
                if (!kv.Key.StartsWith("stream[", StringComparison.Ordinal) || !kv.Key.EndsWith("].Name", StringComparison.Ordinal)) continue;
                if (beforeFlat.ContainsKey(kv.Key) || kv.Value[0] != name) continue;
                string ent = kv.Key.Substring(0, kv.Key.Length - ".Name".Length);
                string[] caller;
                if (!_lastSeen.TryGetValue(ent + ".CallerOid", out caller) || caller[0] != callerOid) continue;
                newOid = ent.Substring("stream[".Length, ent.Length - "stream[".Length - 1);
            }
            r["streamOid"] = newOid;
            r["verified"] = newOid != null;
            return r;
        }

        // stream_remove (unverified live): DeleteChild(<stream name>) on the
        // stream's Parent (the context item), then confirm the OID is gone.
        private static Json.JObj StreamRemove(ActionContext ctx, dynamic sm, AlySnapshot before, Json.JObj result)
        {
            AlyStream st = FindStream(before, ctx.Payload);
            dynamic parent = st.Item.Parent;
            result["stream"] = StreamModel(st, false);
            result["contextPath"] = ComHelpers.SafeStr(delegate { return parent.PathName; });
            if (IsDryRun(ctx.Payload)) { result["written"] = false; return result; }
            var beforeFlat = FlattenAll(before.TianDoc, before.Streams);
            parent.DeleteChild(st.Name);
            ctx.Cache.Invalidate("TIAN");
            Json.JObj r = Finish(sm, ctx.Payload, beforeFlat, null, result);
            // Diff collapses a removed stream to one "stream[<oid>]" entry, so check
            // the post-settle read (_lastSeen) directly.
            r["verified"] = !_lastSeen.ContainsKey("stream[" + st.Oid + "].Name");
            return r;
        }

        // ---- target_remove / stream_edit: offline .tsproj edit ---------------
        // TF3500's model never drops a target that TIAN XML omits and republishes
        // stream Config over the stream item, so these two ops edit the .tsproj on
        // disk while XAE does not hold it (OfflineTsproj) and let TF3500 rebuild its
        // model from the file on load. Disk layout (element names only):
        //   TcSmProject/Project/Analytics/Config/StreamTargets/StreamTargetItem[@Id]
        //   TcSmProject/Project/Analytics/StreamContext[@CallerOid]/Stream[@Id,@oid]/Config/<field>
        private const string DiskAnalytics = "/TcSmProject/Project/Analytics";
        private static readonly System.Text.RegularExpressions.Regex FieldName =
            new System.Text.RegularExpressions.Regex("^[A-Za-z_][A-Za-z0-9_]*$");

        private static Json.JObj TargetRemoveOffline(ActionContext ctx, AlySnapshot before, Json.JObj result)
        {
            string id = NormGuid(ctx.Require("targetId"));
            FindTarget(before.TianDoc, id);
            var users = new List<string>();
            foreach (AlyStream st in before.Streams) if (StreamTargetId(st) == id) users.Add(st.Oid);
            if (users.Count > 0)
            {
                throw new BridgeException("Stream target " + id + " is in use by stream(s) " + string.Join(", ", users.ToArray()) +
                    "; point them at another target first (stream_edit fields.TargetId).");
            }

            var beforeFlat = FlattenAll(before.TianDoc, before.Streams);
            string prefix = "target[" + id + "]";
            var plannedFlat = new Dictionary<string, string[]>();
            foreach (var kv in beforeFlat) if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) plannedFlat[kv.Key] = kv.Value;
            result["targetId"] = id;

            Func<string, string> edit = delegate(string t)
            {
                string xpath = DiskAnalytics + "/Config/StreamTargets/StreamTargetItem";
                List<XmlElement> items = Elements(OfflineTsproj.LoadDom(t), xpath);
                int k = -1;
                for (int i = 0; i < items.Count; i++)
                {
                    if (NormGuid(items[i].GetAttribute("Id")) != id) continue;
                    if (k >= 0) throw new BridgeException("Stream target " + id + " appears twice in the .tsproj");
                    k = i;
                }
                if (k < 0) throw new BridgeException("Stream target " + id + " is not in the .tsproj");
                OfflineTsproj.Span a = OfflineTsproj.Root(t, "Analytics");
                OfflineTsproj.Span targets = OfflineTsproj.Child(t, OfflineTsproj.Child(t, a, "Config", 0), "StreamTargets", 0);
                string edited = OfflineTsproj.RemoveElement(t, OfflineTsproj.Child(t, targets, "StreamTargetItem", k));
                OfflineTsproj.AssertPlanned(t, edited, delegate(XmlDocument d)
                {
                    XmlNode e = d.SelectNodes(xpath)[k];
                    e.ParentNode.RemoveChild(e);
                });
                return edited;
            };
            return RunOffline(ctx, result, beforeFlat, plannedFlat, edit, delegate(Dictionary<string, string[]> after)
            {
                foreach (string key in after.Keys) if (key.StartsWith(prefix, StringComparison.Ordinal)) return false;
                return true;
            });
        }

        private static Json.JObj StreamEditOffline(ActionContext ctx, AlySnapshot before, Json.JObj result)
        {
            Json.JObj p = ctx.Payload;
            AlyStream st = FindStream(before, p);
            var edits = new List<KeyValuePair<string[], string>>();
            Json.JObj fields = p.Obj("fields");
            if (fields != null) FieldEdits(fields, new List<string>(), edits);
            List<string> symbols = null;
            if (p.Arr("symbols") != null)
            {
                symbols = new List<string>();
                foreach (object o in p.Arr("symbols")) symbols.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                edits.Add(new KeyValuePair<string[], string>(new string[] { "SymbolNames" }, EncodeSymbols(symbols)));
            }
            if (edits.Count == 0) throw new BridgeException("fields or symbols is required");

            var beforeFlat = FlattenAll(before.TianDoc, before.Streams);
            var plannedFlat = new Dictionary<string, string[]>(beforeFlat);
            var planned = new Dictionary<string, string>(); // flat key -> planned raw value
            for (int i = 0; i < edits.Count; i++)
            {
                string[] path = edits[i].Key;
                string value = edits[i].Value;
                if (path.Length == 1 && path[0] == "TargetId")
                {
                    value = NormGuid(value);
                    FindTarget(before.TianDoc, value);
                    edits[i] = new KeyValuePair<string[], string>(path, value);
                }
                string key = "stream[" + st.Oid + "]." + string.Join(".", path);
                string raw = path.Length == 1 && path[0] == "SymbolNames" ? string.Join(",", symbols.ToArray()) : value;
                string[] old;
                bool secret = beforeFlat.TryGetValue(key, out old) && old[1] == RedactedValue;
                plannedFlat[key] = new string[] { raw, secret ? RedactedValue : raw };
                planned[key] = raw;
            }
            result["streamOid"] = st.Oid;

            Func<string, string> edit = delegate(string t)
            {
                int[] at = LocateDiskStream(OfflineTsproj.LoadDom(t), st, result);
                string edited = t;
                foreach (var e in edits)
                {
                    OfflineTsproj.Span a = OfflineTsproj.Root(edited, "Analytics");
                    OfflineTsproj.Span s = OfflineTsproj.Child(edited, OfflineTsproj.Child(edited, a, "StreamContext", at[0]), "Stream", at[1]);
                    OfflineTsproj.Span cur = OfflineTsproj.Child(edited, s, "Config", 0);
                    foreach (string part in e.Key) cur = OfflineTsproj.Child(edited, cur, part, 0);
                    edited = OfflineTsproj.SetLeaf(edited, cur, e.Value);
                }
                OfflineTsproj.AssertPlanned(t, edited, delegate(XmlDocument d)
                {
                    XmlNode stream = d.SelectNodes(DiskAnalytics + "/StreamContext")[at[0]].SelectNodes("Stream")[at[1]];
                    foreach (var e in edits)
                    {
                        XmlElement cur = (XmlElement)stream.SelectSingleNode("Config");
                        foreach (string part in e.Key) cur = (XmlElement)cur.SelectSingleNode(part);
                        // SetLeaf keeps <X/> for an empty value; so does the plan.
                        if (e.Value.Length == 0 && cur.IsEmpty) continue;
                        cur.InnerText = e.Value;
                    }
                });
                return edited;
            };
            return RunOffline(ctx, result, beforeFlat, plannedFlat, edit, delegate(Dictionary<string, string[]> after)
            {
                foreach (var kv in planned)
                {
                    string[] got;
                    if (!after.TryGetValue(kv.Key, out got)) return false;
                    bool same = kv.Key.EndsWith(".TargetId", StringComparison.Ordinal) ? NormGuid(got[0]) == kv.Value : got[0] == kv.Value;
                    if (!same) return false;
                }
                return true;
            });
        }

        // The stream's element on disk: the Stream whose @Id or @oid is the live OID,
        // else the one Stream with the live name under the context with its CallerOid
        // (a live OID resolved through TIAN AdiOids need not be the element's own).
        private static int[] LocateDiskStream(XmlDocument dom, AlyStream st, Json.JObj result)
        {
            List<XmlElement> contexts = Elements(dom, DiskAnalytics + "/StreamContext");
            int[] byOid = null;
            int[] byName = null;
            int nameHits = 0;
            for (int ci = 0; ci < contexts.Count; ci++)
            {
                List<XmlElement> streams = Elements(contexts[ci], "Stream");
                for (int si = 0; si < streams.Count; si++)
                {
                    XmlElement s = streams[si];
                    if (NormOid(s.GetAttribute("Id")) == st.Oid || NormOid(s.GetAttribute("oid")) == st.Oid)
                    {
                        if (byOid != null) throw new BridgeException("Stream " + st.Oid + " appears twice in the .tsproj");
                        byOid = new int[] { ci, si };
                    }
                    if (NormOid(contexts[ci].GetAttribute("CallerOid")) == st.CallerOid && Text(s, "Name") == st.Name)
                    {
                        byName = new int[] { ci, si };
                        nameHits++;
                    }
                }
            }
            if (byOid != null) { result["diskMatch"] = "oid"; return byOid; }
            if (nameHits == 1) { result["diskMatch"] = "callerOid+name"; return byName; }
            throw new BridgeException("Stream " + st.Oid + " ('" + st.Name + "') is not " + (nameHits > 1 ? "unique" : "present") + " in the .tsproj");
        }

        // fields {A: v, "B.C": v, D: {E: v}} -> (element path under the stream Config, text).
        private static void FieldEdits(Json.JObj values, List<string> prefix, List<KeyValuePair<string[], string>> into)
        {
            foreach (var kv in values)
            {
                var path = new List<string>(prefix);
                foreach (string part in kv.Key.Split('.'))
                {
                    if (!FieldName.IsMatch(part)) throw new BridgeException("Invalid field name '" + kv.Key + "'");
                    path.Add(part);
                }
                Json.JObj nested = kv.Value as Json.JObj;
                if (nested != null) { FieldEdits(nested, path, into); continue; }
                string leaf = path[path.Count - 1];
                if (leaf.EndsWith("Crypted", StringComparison.Ordinal)) throw new BridgeException("'" + string.Join(".", path.ToArray()) + "' is an XAE-encrypted value and cannot be set.");
                if (path.Count == 1 && leaf == "SymbolNames") throw new BridgeException("Set SymbolNames through symbols (a list of symbol names), not fields.");
                into.Add(new KeyValuePair<string[], string>(path.ToArray(), XmlValue(kv.Value)));
            }
        }

        // TF3500's SymbolNames: base64 of the UTF-8 names, each followed by a NUL.
        private static string EncodeSymbols(List<string> names)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string n in names)
            {
                if (n.Length == 0 || n.IndexOf('\0') >= 0) throw new BridgeException("symbols must be non-empty names without NUL");
                sb.Append(n).Append('\0');
            }
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // dryRun: plannedDiff, the mode, and the text edit run against the file in
        // memory (nothing is written, unloaded or closed). Otherwise the offline
        // edit, then the usual settled re-read; verified = applied(settled state).
        private static Json.JObj RunOffline(ActionContext ctx, Json.JObj result, Dictionary<string, string[]> beforeFlat,
            Dictionary<string, string[]> plannedFlat, Func<string, string> edit, Func<Dictionary<string, string[]>, bool> applied)
        {
            result["plannedDiff"] = Diff(beforeFlat, plannedFlat);
            OfflineTsproj.Plan plan = OfflineTsproj.Prepare(ctx);
            result["mode"] = plan.Mode;
            result["tsproj"] = plan.TsprojPath;
            if (IsDryRun(ctx.Payload))
            {
                if (plan.Unsaved.Count > 0) result["unsaved"] = new Json.JArr(plan.Unsaved.Cast<object>());
                else
                {
                    bool bom;
                    edit(OfflineTsproj.Decode(File.ReadAllBytes(plan.TsprojPath), out bom));
                }
                result["fileEditChecked"] = plan.Unsaved.Count == 0;
                result["written"] = false;
                return result;
            }
            dynamic sm = OfflineTsproj.Apply(ctx, plan, edit, result);
            Json.JObj r = Finish(sm, ctx.Payload, beforeFlat, plannedFlat, result);
            r["verified"] = applied(_lastSeen);
            return r;
        }

        private static bool IsDryRun(Json.JObj p) { return p.Bool("dryRun", false); }

        // ===================================================================
        // private helpers
        // ===================================================================

        // Get-XaePublicAssembliesPath (bridge L64-75): the XAE PublicAssemblies
        // dir, used as a last-ditch probe location for the measurement assembly.
        private static string GetXaePublicAssembliesPath()
        {
            string[] candidates = new string[] {
                "C:\\Program Files (x86)\\Beckhoff\\TcXaeShell\\Common7\\IDE\\PublicAssemblies",
                "C:\\Program Files\\Beckhoff\\TcXaeShell\\Common7\\IDE\\PublicAssemblies"
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "envdte.dll"))) return candidate;
            }
            return candidates[0];
        }

        // Get-MeasurementLibPath (bridge L859-875): resolve the TE130X Scope View
        // Automation Interface assembly across known install dirs; null if absent.
        private static string GetMeasurementLibPath()
        {
            string name = "TwinCAT.Measurement.AutomationInterface.dll";
            string[] dirs = new string[] {
                "C:\\TwinCAT\\Functions\\TE130X-Scope-View",
                "C:\\Program Files (x86)\\Beckhoff\\TwinCAT\\Functions\\TE130X-Scope-View",
                "C:\\Program Files\\Beckhoff\\TwinCAT\\Functions\\TE130X-Scope-View",
                GetXaePublicAssembliesPath()
            };
            foreach (string dir in dirs)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                string direct = Path.Combine(dir, name);
                if (File.Exists(direct)) return direct;
                try
                {
                    string[] hits = Directory.GetFiles(dir, name, SearchOption.AllDirectories);
                    if (hits != null && hits.Length > 0) return hits[0];
                }
                catch { }
            }
            return null;
        }

        // Get-ScopeTemplatePath (bridge L879-891): probe Templates\Projects for a
        // *.tcmproj; null if the tooling is not installed.
        private static string GetScopeTemplatePath()
        {
            string[] dirs = new string[] {
                "C:\\TwinCAT\\Functions\\TE130X-Scope-View\\Templates\\Projects",
                "C:\\Program Files (x86)\\Beckhoff\\TwinCAT\\Functions\\TE130X-Scope-View\\Templates\\Projects",
                "C:\\Program Files\\Beckhoff\\TwinCAT\\Functions\\TE130X-Scope-View\\Templates\\Projects"
            };
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    string[] hits = Directory.GetFiles(dir, "*.tcmproj", SearchOption.TopDirectoryOnly);
                    if (hits != null && hits.Length > 0) return hits[0];
                }
                catch { }
            }
            return null;
        }

        // Get-AnalyticsTemplatePath (bridge L895-911): probe the Analytics product
        // template dirs for *.tcaproj/*.tcanalyticsproj/*.tsproj under a Templates
        // path segment; null if none found.
        private static string GetAnalyticsTemplatePath()
        {
            string[] roots = new string[] {
                "C:\\TwinCAT\\Functions\\TE3500-Analytics-Workbench",
                "C:\\TwinCAT\\Functions\\TE3520-Analytics-Service-Tool",
                "C:\\Program Files (x86)\\Beckhoff\\TwinCAT\\Functions\\TE3500-Analytics-Workbench",
                "C:\\Program Files (x86)\\Beckhoff\\TwinCAT\\Functions\\TE3520-Analytics-Service-Tool"
            };
            string[] patterns = new string[] { "*.tcaproj", "*.tcanalyticsproj", "*.tsproj" };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string pat in patterns)
                {
                    string[] hits;
                    try { hits = Directory.GetFiles(root, pat, SearchOption.AllDirectories); }
                    catch { hits = null; }
                    if (hits == null) continue;
                    foreach (string hit in hits)
                    {
                        if (hit.IndexOf("\\Templates\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            hit.IndexOf("/Templates/", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return hit;
                        }
                    }
                }
            }
            return null;
        }

        // Ensure-MeasurementScopeHelper (bridge L923-1018): load the TE130X
        // automation assembly so ScopeHelper can resolve IMeasurementScope by
        // reflection. Returns false (and callers throw 'tooling not installed') if
        // the assembly cannot be located/loaded.
        private static bool EnsureScopeHelper()
        {
            if (ScopeHelper.Available()) return true;
            string libPath = GetMeasurementLibPath();
            if (libPath == null) return false;
            try { Assembly.LoadFrom(libPath); }
            catch { return false; }
            return ScopeHelper.Available();
        }

        // Get-ScopeProjectObject (bridge L1023-1045): find the EnvDTE.Project by
        // Name in the open solution and return its .Object (the IMeasurementScope
        // automation object). These are SEPARATE EnvDTE.Project nodes, not System
        // Manager tree items.
        private static object GetScopeProjectObject(dynamic dte, string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName)) throw new BridgeException("project is required");
            dynamic solution = dte.Solution;
            if (solution == null || !((bool)solution.IsOpen)) throw new BridgeException("No solution is open");

            dynamic projects = solution.Projects;
            if (projects != null)
            {
                int count = (int)projects.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic proj = null;
                    try { proj = projects.Item(i); }
                    catch { continue; }
                    if (proj == null) continue;
                    string pname = ComHelpers.SafeStr(delegate { return proj.Name; });
                    if (pname == projectName)
                    {
                        object obj = null;
                        try { obj = proj.Object; }
                        catch { }
                        if (obj == null) throw new BridgeException("Scope project '" + projectName + "' has no automation object (.Object is null)");
                        return obj;
                    }
                }
            }
            throw new BridgeException("Scope project not found in the open solution: " + projectName);
        }

        // Resolve-ScopeElement (bridge L1051-1069): walk a '^'-separated parentPath
        // of element names from the scope root, resolving each segment by name via
        // child enumeration. Empty path returns the root. LookUpChild is UNVERIFIED
        // and deliberately unused; enumeration is the only resolution.
        private static object ResolveScopeElement(object root, string elementPath)
        {
            object current = root;
            if (string.IsNullOrWhiteSpace(elementPath)) return current;
            string[] segments = elementPath.Split('^');
            foreach (string seg in segments)
            {
                if (string.IsNullOrWhiteSpace(seg)) continue;
                object[] children = ScopeHelper.Children(current);
                object match = null;
                foreach (object c in children)
                {
                    string cn = ScopeHelper.NameOf(c);
                    if (cn == seg) { match = c; break; }
                }
                if (match == null)
                {
                    throw new BridgeException("Scope element segment not found by name: '" + seg + "' (path '" + elementPath +
                        "'). Child enumeration is the only verified resolution; LookUpChild is unsupported. Restrict the path to existing named children.");
                }
                current = match;
            }
            return current;
        }

        // Known node names for a TIAN StreamHelper created as <name>.
        private static string[] StreamHelperNames(string name)
        {
            return new string[] { name + " (StreamHelper)", name + "_Obj1 (StreamHelper)", name };
        }

        // Get-ChildTreeItemByName equivalent: true if a direct child of the parent
        // tree item has the given name. Used by analytics dry-run.
        private static bool ChildExistsByName(dynamic parentItem, string childName)
        {
            foreach (dynamic child in ComHelpers.Children(parentItem))
            {
                string name = ComHelpers.SafeStr(delegate { return child.Name; });
                if (name == childName) return true;
            }
            return false;
        }

        // Assert-WellFormedChild (bridge L3192-3241): validate a child returned by
        // ITcSmTreeItem.CreateChild; on a malformed "ghost" do best-effort cleanup
        // (DeleteChild by the actual non-blank name) and THROW a descriptive error.
        // (Mirrors TreeActions.AssertWellFormedChild; duplicated to keep this group
        // self-contained.)
        private static void AssertWellFormedChild(dynamic parent, dynamic child, string requestedName, int subType, string parentPath, string[] acceptedNames)
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
            else if (Array.IndexOf(acceptedNames, childActualName) < 0)
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
                "') for requested name='" + requestedName + "', subType=" + subType.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " under '" + parentPath + "' (" + reason + "). This usually means the subType/createInfo is not valid for this parent " +
                "(EtherCAT boxes typically require a proper createInfo). No usable child was created. If a stray blank-named child remains, " +
                "remove it in the XAE GUI or via close-without-save.");
        }

        // ===================================================================
        // ScopeHelper — C# port of the compiled Te1000MeasurementHelper shim
        // (bridge L936-1016). IMeasurementScope is a vtable/IUnknown interface
        // that cannot be late-bound via `dynamic`, so its members are invoked by
        // reflection against the interface type discovered (by name) in the loaded
        // TE130X automation assembly. Only the VERIFIED surface is exposed
        // (CreateChild/ChangeName/StartRecord/StopRecord); SaveSVD/ExportCSV/
        // LookUpChild are deliberately NOT exposed (UNVERIFIED).
        // ===================================================================
        private static class ScopeHelper
        {
            // Find the IMeasurementScope interface across all loaded assemblies.
            private static Type ScopeType()
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try
                    {
                        t = a.GetTypes().FirstOrDefault(delegate(Type x) { return x.IsInterface && x.Name == "IMeasurementScope"; });
                    }
                    catch { t = null; }
                    if (t != null) return t;
                }
                return null;
            }

            // True once the IMeasurementScope interface type is loadable.
            public static bool Available()
            {
                return ScopeType() != null;
            }

            public static bool Is(object o)
            {
                if (o == null) return false;
                Type t = ScopeType();
                return t != null && t.IsInstanceOfType(o);
            }

            // CreateChild(out object child, string name, int elementType) -> int rc.
            public static int CreateChild(object scope, out object child, string name, int elementType)
            {
                child = null;
                Type t = ScopeType();
                if (t == null) throw new InvalidOperationException("IMeasurementScope type not found");
                MethodInfo m = t.GetMethod("CreateChild");
                if (m == null) throw new MissingMethodException("IMeasurementScope.CreateChild");
                object[] args = new object[] { null, name == null ? "" : name, elementType };
                object rc = m.Invoke(scope, args);
                child = args[0];
                return rc == null ? 0 : Convert.ToInt32(rc);
            }

            public static int ChangeName(object el, string n)
            {
                Type t = ScopeType();
                MethodInfo m = t.GetMethod("ChangeName");
                if (m == null) throw new MissingMethodException("IMeasurementScope.ChangeName");
                object rc = m.Invoke(el, new object[] { n });
                return rc == null ? 0 : Convert.ToInt32(rc);
            }

            public static int StartRecord(object s)
            {
                Type t = ScopeType();
                MethodInfo m = t.GetMethod("StartRecord");
                if (m == null) throw new MissingMethodException("IMeasurementScope.StartRecord");
                object rc = m.Invoke(s, null);
                return rc == null ? 0 : Convert.ToInt32(rc);
            }

            public static int StopRecord(object s)
            {
                Type t = ScopeType();
                MethodInfo m = t.GetMethod("StopRecord");
                if (m == null) throw new MissingMethodException("IMeasurementScope.StopRecord");
                object rc = m.Invoke(s, null);
                return rc == null ? 0 : Convert.ToInt32(rc);
            }

            // Enumerate a parent scope element's children for name-walking. Tries
            // common collection members; returns an empty array if none resolve.
            public static object[] Children(object el)
            {
                if (el == null) return new object[0];
                Type t = el.GetType();
                string[] props = new string[] { "Children", "ChildCollection", "Items" };
                foreach (string p in props)
                {
                    try
                    {
                        PropertyInfo pi = t.GetProperty(p);
                        if (pi != null)
                        {
                            object col = pi.GetValue(el, null);
                            System.Collections.IEnumerable en = col as System.Collections.IEnumerable;
                            if (en != null)
                            {
                                return en.Cast<object>().ToArray();
                            }
                        }
                    }
                    catch { }
                }
                return new object[0];
            }

            public static string NameOf(object el)
            {
                if (el == null) return null;
                try
                {
                    PropertyInfo pi = el.GetType().GetProperty("Name");
                    if (pi != null)
                    {
                        object v = pi.GetValue(el, null);
                        return v == null ? null : v.ToString();
                    }
                }
                catch { }
                return null;
            }
        }
    }
}
