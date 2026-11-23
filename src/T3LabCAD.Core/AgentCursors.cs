using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>What kind of work a call does: picks the colour on the bar and on the drawing.</summary>
    internal enum ActionKind { Read, Create, Modify, Delete, File, View, Command }

    internal enum StepState { Running, Ok, Failed, Refused }

    /// <summary>One tool call as the Agent Cursors bar shows it.</summary>
    internal sealed class AgentStep
    {
        public string Agent, Tool, Caption, Error;
        public ActionKind Action;
        public DateTime Started = DateTime.Now;
        public double Seconds;
        public StepState State = StepState.Running;
        public readonly List<string> NamedHandles = new List<string>();   // from the arguments and the result
        public readonly List<ObjectId> Created = new List<ObjectId>(), Modified = new List<ObjectId>();
        public readonly List<Extents3d> ErasedBoxes = new List<Extents3d>();
        public int ErasedCount;
        public string Document = "";
    }

    internal sealed class AgentInfo
    {
        public string Key, Name;
        public System.Drawing.Color Color;
        public string Goal;
        public List<string> Tasks = new List<string>();
        public int Step;                 // 1-based task in progress, 0 = none
        public bool PlanDone;
        public AgentStep Current;
        public DateTime LastSeen = DateTime.Now;
        public int Calls, Changes;
    }

    /// <summary>
    /// Agent Cursors for AutoCAD: a live view of what each AI app does in the drawing through MCP, after the
    /// T3Lab Revit feature of the same name (idea from swarm-cursors; no code from it). The bar is a palette
    /// (ActivityPalette); the drawing overlay is transient graphics (CursorOverlay), off by default.
    /// Off by default as a whole (T3CURSORS or the ribbon button). Begin / End run on the socket thread and only
    /// record; every UI update is posted to the main thread and can never fail a tool call.
    /// </summary>
    internal static class AgentCursors
    {
        public const string NarrateTool = "t3lab_narrate";
        const int FeedSize = 20, PlanMax = 8, TaskMax = 60, GoalMax = 90, MaxCapture = 200;

        static readonly object Gate = new object();
        static readonly Dictionary<string, AgentInfo> Agents = new Dictionary<string, AgentInfo>(StringComparer.OrdinalIgnoreCase);
        static readonly List<AgentStep> Feed = new List<AgentStep>();
        static readonly HashSet<string> Touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // handles changed this session
        static ActivityPalette _palette;
        static CursorOverlay _overlay;
        static bool _hinted;

        public static volatile bool Enabled, Paused;
        public static volatile bool ShowOnDrawing, ZoomToWork;

        [ThreadStatic] public static string CurrentAgent;

        static string SettingsFile => Path.Combine(Log.DataDir, "agent_cursors.json");

        // ---------------------------------------------------------------- settings

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                var a = new Args(Json.Parse(File.ReadAllText(SettingsFile)));
                Enabled = a.Bool("enabled", false);
                ShowOnDrawing = a.Bool("show_on_drawing", false);
                ZoomToWork = a.Bool("zoom_to_work", false);
            }
            catch (Exception ex) { Log.Error("agent cursors settings: " + ex.Message); }
            if (Enabled) Entry.Main?.Post(ShowUi);
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile, Json.Write(new Dictionary<string, object>
                {
                    ["enabled"] = Enabled,
                    ["show_on_drawing"] = ShowOnDrawing,
                    ["zoom_to_work"] = ZoomToWork,
                }));
            }
            catch (Exception ex) { Log.Error("agent cursors settings: " + ex.Message); }
        }

        /// <summary>T3CURSORS / ribbon: on, off. Main thread.</summary>
        public static string Toggle()
        {
            SetEnabled(!Enabled);
            return Enabled
                ? "T3Lab Agent Cursors: on. The bar shows what each AI app does; tick \"Show on the drawing\" for cursors and boxes."
                : "T3Lab Agent Cursors: off.";
        }

        public static void SetEnabled(bool on)
        {
            Enabled = on;
            if (!on) Paused = false;
            Save();
            if (on) ShowUi();
            else HideUi();
        }

        public static void SetOption(string name, bool value)
        {
            if (name == "show_on_drawing") { ShowOnDrawing = value; if (!value) _overlay?.Clear(); }
            else if (name == "zoom_to_work") ZoomToWork = value;
            Save();
            Refresh();
        }

        public static void SetPaused(bool paused)
        {
            Paused = paused;
            Refresh();
        }

        static void ShowUi()
        {
            try
            {
                if (_palette == null || !_palette.Alive) _palette = new ActivityPalette();
                _palette.Show(Snapshot());
            }
            catch (Exception ex) { Log.Error("agent cursors bar: " + ex); }
        }

        static void HideUi()
        {
            try { _palette?.Dispose(); } catch (Exception) { }
            _palette = null;
            try { _overlay?.Dispose(); } catch (Exception) { }
            _overlay = null;
        }

        public static void Shutdown()
        {
            HideUi();
            lock (Gate)
            {
                Agents.Clear();
                Feed.Clear();
            }
        }

        // ---------------------------------------------------------------- agents

        static readonly System.Drawing.Color[] Palette =
        {
            System.Drawing.Color.FromArgb(0xD9, 0x77, 0x57), System.Drawing.Color.FromArgb(0x0D, 0x99, 0xFF),
            System.Drawing.Color.FromArgb(0x14, 0xAE, 0x5C), System.Drawing.Color.FromArgb(0xA2, 0x59, 0xFF),
            System.Drawing.Color.FromArgb(0xE8, 0x46, 0x7C), System.Drawing.Color.FromArgb(0x00, 0xB5, 0xCE),
            System.Drawing.Color.FromArgb(0xFF, 0xC7, 0x00), System.Drawing.Color.FromArgb(0xFF, 0x72, 0x62),
        };

        /// <summary>Display name and colour of an AI app from its MCP clientInfo.name.</summary>
        static AgentInfo AgentFor(string rawName)
        {
            string key = string.IsNullOrWhiteSpace(rawName) ? "ai" : rawName.Trim();
            if (Agents.TryGetValue(key, out AgentInfo known)) return known;
            string lower = key.ToLowerInvariant();
            string name;
            System.Drawing.Color color;
            if (lower.Contains("claude-code") || lower == "claude code") { name = "Claude Code"; color = Palette[0]; }
            else if (lower.Contains("claude")) { name = "Claude"; color = Palette[0]; }
            else if (lower.Contains("cursor")) { name = "Cursor"; color = Palette[3]; }
            else if (lower.Contains("codex") || lower.Contains("openai") || lower.Contains("chatgpt")) { name = "ChatGPT"; color = Palette[2]; }
            else if (lower.Contains("gemini") || lower.Contains("antigravity")) { name = "Gemini"; color = Palette[1]; }
            else if (lower.Contains("copilot") || lower.Contains("vscode")) { name = "Copilot"; color = Palette[5]; }
            else if (key == "ai") { name = "AI"; color = Palette[1]; }
            else
            {
                name = key.Length > 16 ? key.Substring(0, 16) : key;
                color = Palette[Math.Abs(key.GetHashCode()) % Palette.Length];
            }
            var info = new AgentInfo { Key = key, Name = name, Color = color };
            Agents[key] = info;
            return info;
        }

        public static string ClientName(object raw)
        {
            if (raw is Dictionary<string, object> d) return new Args(d).Str("name");
            return raw as string;
        }

        // ---------------------------------------------------------------- calls

        /// <summary>A call is starting (socket thread). Returns the step to pass to End, or null when off.</summary>
        public static AgentStep Begin(ToolDef def, Args args, string client)
        {
            if (!Enabled)
            {
                if (!_hinted && def.Name != NarrateTool)
                {
                    _hinted = true;
                    string who = AgentForName(client);
                    Entry.Main?.Post(() => Write($"T3Lab: {who} is working in AutoCAD through MCP. Type T3CURSORS to see what it does (Agent Cursors)."));
                }
                return null;
            }
            if (def.Name == NarrateTool) return null;
            var step = new AgentStep
            {
                Tool = def.Name,
                Action = ActionOf(def, args),
                Caption = Describe(def, args),
                Document = Entry.ActiveDocument,
            };
            step.NamedHandles.AddRange(HandlesIn(args.Raw("handles")));
            step.NamedHandles.AddRange(HandlesIn(args.Raw("handle")));
            lock (Gate)
            {
                AgentInfo agent = AgentFor(client);
                step.Agent = agent.Key;
                agent.Current = step;
                agent.LastSeen = DateTime.Now;
                agent.Calls++;
            }
            Refresh();
            return step;
        }

        static string AgentForName(string client)
        {
            lock (Gate) return AgentFor(client).Name;
        }

        /// <summary>A call ended (socket thread): record it, then show it.</summary>
        public static void End(AgentStep step, bool ok, bool refused, string error, object result)
        {
            if (step == null) return;
            step.Seconds = (DateTime.Now - step.Started).TotalSeconds;
            step.State = refused ? StepState.Refused : ok ? StepState.Ok : StepState.Failed;
            step.Error = error;
            if (result != null) CollectResultHandles(result, step.NamedHandles, 0);
            AgentInfo agent;
            lock (Gate)
            {
                agent = Agents.TryGetValue(step.Agent, out AgentInfo a) ? a : null;
                if (agent != null)
                {
                    if (agent.Current == step) agent.Current = null;
                    agent.LastSeen = DateTime.Now;
                    agent.Changes += step.Created.Count + step.Modified.Count + step.ErasedCount;
                }
                Feed.Insert(0, step);
                if (Feed.Count > FeedSize) Feed.RemoveAt(Feed.Count - 1);
                foreach (ObjectId id in step.Created.Concat(step.Modified)) Touched.Add(id.Handle.ToString());
                if (step.Action != ActionKind.Read) foreach (string h in step.NamedHandles) Touched.Add(h);
            }
            Entry.Main?.Post(() =>
            {
                RefreshNow();
                if (ShowOnDrawing && step.State == StepState.Ok && agent != null) ShowOnModel(agent, step);
            });
        }

        /// <summary>The goal / task banner (t3lab_narrate). Any thread.</summary>
        public static void Narrate(string client, string goal, List<string> tasks, int step, bool done)
        {
            lock (Gate)
            {
                AgentInfo agent = AgentFor(client);
                if (goal != null) agent.Goal = Clip(goal, GoalMax);
                if (tasks != null && tasks.Count > 0)
                {
                    agent.Tasks = tasks.Take(PlanMax).Select(t => Clip(t, TaskMax)).ToList();
                    agent.Step = 1;
                    agent.PlanDone = false;
                }
                if (step > 0) agent.Step = Math.Min(step, Math.Max(1, agent.Tasks.Count));
                if (done) { agent.PlanDone = true; agent.Step = agent.Tasks.Count; }
                agent.LastSeen = DateTime.Now;
            }
            Refresh();
        }

        static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        // ---------------------------------------------------------------- change capture

        /// <summary>Entities appended / changed / erased in the drawing while a writing call runs (also by run_command). Main thread.</summary>
        public sealed class Capture : IDisposable
        {
            readonly AgentStep _step;
            readonly Database _db;
            readonly HashSet<ObjectId> _seen = new HashSet<ObjectId>();

            public Capture(AgentStep step, Database db)
            {
                _step = step;
                _db = db;
                db.ObjectAppended += OnAppended;
                db.ObjectModified += OnModified;
                db.ObjectErased += OnErased;
            }

            bool Room() => _step.Created.Count + _step.Modified.Count + _step.ErasedBoxes.Count < MaxCapture;

            void OnAppended(object sender, ObjectEventArgs e)
            {
                if (e.DBObject is Entity && Room() && _seen.Add(e.DBObject.ObjectId)) lock (Gate) _step.Created.Add(e.DBObject.ObjectId);
            }

            void OnModified(object sender, ObjectEventArgs e)
            {
                if (!(e.DBObject is Entity) || e.DBObject is AttributeReference) return;
                if (Room() && _seen.Add(e.DBObject.ObjectId)) lock (Gate) _step.Modified.Add(e.DBObject.ObjectId);
            }

            void OnErased(object sender, ObjectErasedEventArgs e)
            {
                if (!e.Erased || !(e.DBObject is Entity ent)) return;
                lock (Gate)
                {
                    _step.ErasedCount++;
                    _step.Created.Remove(ent.ObjectId);
                    _step.Modified.Remove(ent.ObjectId);
                    if (_step.ErasedBoxes.Count < 12)
                    {
                        try
                        {
                            Extents3d? b = ent.Bounds;
                            if (b.HasValue) _step.ErasedBoxes.Add(b.Value);
                        }
                        catch (Exception) { /* no extents for this one */ }
                    }
                }
            }

            public void Dispose()
            {
                try
                {
                    _db.ObjectAppended -= OnAppended;
                    _db.ObjectModified -= OnModified;
                    _db.ObjectErased -= OnErased;
                }
                catch (Exception) { /* the drawing was closed */ }
            }
        }

        // ---------------------------------------------------------------- captions

        static readonly Dictionary<string, ActionKind> Kinds = new Dictionary<string, ActionKind>
        {
            ["add_entities"] = ActionKind.Create, ["create_block"] = ActionKind.Create, ["create_layout"] = ActionKind.Create,
            ["add_viewport"] = ActionKind.Create, ["attach_xref"] = ActionKind.Create, ["attach_underlay"] = ActionKind.Create,
            ["attach_point_cloud"] = ActionKind.Create, ["civil_points"] = ActionKind.Create, ["import_from_drawing"] = ActionKind.Create,
            ["delete_entities"] = ActionKind.Delete, ["delete_layer"] = ActionKind.Delete, ["delete_object"] = ActionKind.Delete,
            ["purge"] = ActionKind.Delete, ["delete_drawing_file"] = ActionKind.Delete,
            ["open_document"] = ActionKind.File, ["new_document"] = ActionKind.File, ["save_document"] = ActionKind.File,
            ["close_document"] = ActionKind.File, ["activate_document"] = ActionKind.File, ["rename_document"] = ActionKind.File,
            ["export_pdf"] = ActionKind.File, ["publish_pdf"] = ActionKind.File, ["export_entities"] = ActionKind.File,
            ["list_drawing_files"] = ActionKind.File,
            ["zoom"] = ActionKind.View, ["screenshot"] = ActionKind.View, ["restore_view"] = ActionKind.View,
            ["activate_layout"] = ActionKind.View, ["selection"] = ActionKind.View, ["set_ucs"] = ActionKind.View,
            ["run_command"] = ActionKind.Command, ["cancel_command"] = ActionKind.Command,
        };

        static readonly Dictionary<string, string> Verbs = new Dictionary<string, string>
        {
            ["get_status"] = "Checking AutoCAD", ["query_entities"] = "Reading entities", ["get_texts"] = "Reading texts",
            ["list_layers"] = "Reading layers", ["list_blocks"] = "Reading blocks", ["list_layouts"] = "Reading layouts",
            ["list_named_objects"] = "Reading named objects", ["measure"] = "Measuring", ["quantity_takeoff"] = "Counting quantities",
            ["extract_block_data"] = "Reading block data", ["screenshot"] = "Looking at the drawing", ["zoom"] = "Zooming",
            ["add_entities"] = "Drawing", ["update_entities"] = "Changing", ["delete_entities"] = "Deleting",
            ["explode_entities"] = "Exploding", ["create_block"] = "Making a block", ["replace_text"] = "Replacing text",
            ["sync_block_attributes"] = "Syncing attributes", ["set_layer"] = "Setting a layer", ["delete_layer"] = "Deleting a layer",
            ["set_text_style"] = "Setting a text style", ["set_dim_style"] = "Setting a dimension style", ["purge"] = "Purging",
            ["open_document"] = "Opening a drawing", ["new_document"] = "Creating a drawing", ["save_document"] = "Saving",
            ["close_document"] = "Closing a drawing", ["export_pdf"] = "Exporting PDF", ["publish_pdf"] = "Publishing PDF",
            ["run_command"] = "Running a command", ["import_from_drawing"] = "Importing", ["attach_xref"] = "Attaching an xref",
            ["set_viewport"] = "Setting a viewport", ["add_viewport"] = "Adding a viewport", ["create_layout"] = "Creating a layout",
        };

        static ActionKind ActionOf(ToolDef def, Args args)
        {
            if (def.Name == "transform_entities")
                return args.Bool("copy", false) || (args.Str("operation") ?? "").StartsWith("array", StringComparison.OrdinalIgnoreCase)
                    ? ActionKind.Create : ActionKind.Modify;
            if (def.Name == "replace_text" && args.Bool("dry_run", false)) return ActionKind.Read;
            if (Kinds.TryGetValue(def.Name, out ActionKind k)) return k;
            switch (def.Kind)
            {
                case ToolKind.Read: return ActionKind.Read;
                case ToolKind.App: return ActionKind.File;
                default: return ActionKind.Modify;
            }
        }

        /// <summary>A plain caption: "Drawing 3 entities", "Moving 12 entities", "Running LINE", "Exporting PDF".</summary>
        static string Describe(ToolDef def, Args args)
        {
            int count = 0;
            if (args.List("items") is IList items) count = items.Count;
            else if (args.List("handles") is IList handles) count = handles.Count;
            else if (args.List("names") is IList names) count = names.Count;
            string things = count == 1 ? "1 entity" : count + " entities";
            switch (def.Name)
            {
                case "transform_entities":
                {
                    string op = (args.Str("operation") ?? "").ToLowerInvariant();
                    string verb = op == "move" ? "Moving" : op == "rotate" ? "Rotating" : op == "scale" ? "Scaling" : op == "mirror" ? "Mirroring"
                        : op == "align" ? "Aligning" : op.StartsWith("array", StringComparison.Ordinal) ? "Arraying" : "Transforming";
                    if (args.Bool("copy", false) && !op.StartsWith("array", StringComparison.Ordinal)) verb = "Copying";
                    return verb + " " + things;
                }
                case "edit_geometry":
                {
                    string op = (args.Str("operation") ?? "geometry").ToLowerInvariant();
                    return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(op) + (count > 0 ? " · " + things : "");
                }
                case "run_command":
                {
                    string cmd = (args.Str("command") ?? "").Trim().Split(' ', '\n')[0].TrimStart('_', '.', '-');
                    return "Running " + cmd.ToUpperInvariant();
                }
                case "delete_object":
                case "rename_object":
                    return (def.Name == "delete_object" ? "Deleting " : "Renaming ") + (args.Str("kind") ?? "object").Replace('_', ' ') +
                           (count > 1 ? " ×" + count : args.Str("name") != null ? " " + args.Str("name") : "");
                case "replace_text":
                    return (args.Bool("dry_run", false) ? "Previewing" : "Replacing") + " \"" + Clip(args.Str("find") ?? "", 20) + "\"";
            }
            if (Verbs.TryGetValue(def.Name, out string caption))
                return count > 0 && (def.Name.EndsWith("_entities", StringComparison.Ordinal)) ? caption + " " + things : caption;
            string human = def.Name.Replace('_', ' ');
            return char.ToUpperInvariant(human[0]) + human.Substring(1);
        }

        static IEnumerable<string> HandlesIn(object raw)
        {
            if (raw is string s && s.Length > 0 && s.Length <= 16) yield return s;
            else if (raw is IList list)
                foreach (object o in list)
                    if (o is string h && h.Length <= 16) yield return h;
        }

        static readonly HashSet<string> HandleKeys = new HashSet<string> { "handle", "handles", "new_handles", "insert_handle" };

        /// <summary>Handles a result names (created / changed entities), a few levels deep.</summary>
        static void CollectResultHandles(object value, List<string> into, int depth)
        {
            if (depth > 4 || into.Count > MaxCapture) return;
            switch (value)
            {
                case Dictionary<string, object> d:
                    foreach (var kv in d)
                    {
                        if (HandleKeys.Contains(kv.Key)) into.AddRange(HandlesIn(kv.Value));
                        else if (kv.Value is IDictionary || (kv.Value is IList && !(kv.Value is string))) CollectResultHandles(kv.Value, into, depth + 1);
                    }
                    break;
                case IList list:
                    foreach (object o in list) CollectResultHandles(o, into, depth + 1);
                    break;
            }
        }

        // ---------------------------------------------------------------- UI glue (main thread)

        internal sealed class AgentView
        {
            public string Name, Status, Goal, Now;
            public System.Drawing.Color Color;
            public List<string> Tasks;
            public int Step;
            public bool PlanDone, Working;
            public ActionKind NowAction;
            public double NowSeconds;
            public int Calls, Changes;
        }

        internal sealed class StepView
        {
            public string Agent, Caption, Error;
            public System.Drawing.Color Color;
            public ActionKind Action;
            public StepState State;
            public double Seconds;
            public DateTime When;
            public int Changed;
        }

        internal sealed class View
        {
            public List<AgentView> Agents = new List<AgentView>();
            public List<StepView> Feed = new List<StepView>();
            public bool Paused, ShowOnDrawing, ZoomToWork;
            public int Touched;
        }

        internal static View Snapshot()
        {
            lock (Gate)
            {
                var v = new View { Paused = Paused, ShowOnDrawing = ShowOnDrawing, ZoomToWork = ZoomToWork, Touched = Touched.Count };
                foreach (AgentInfo a in Agents.Values.OrderByDescending(x => x.LastSeen))
                {
                    AgentStep cur = a.Current;
                    v.Agents.Add(new AgentView
                    {
                        Name = a.Name,
                        Color = a.Color,
                        Goal = a.Goal,
                        Tasks = a.Tasks.ToList(),
                        Step = a.Step,
                        PlanDone = a.PlanDone,
                        Working = cur != null,
                        Now = cur?.Caption,
                        NowAction = cur?.Action ?? ActionKind.Read,
                        NowSeconds = cur == null ? 0 : (DateTime.Now - cur.Started).TotalSeconds,
                        Status = Paused ? "Paused" : cur != null ? "Working" : (DateTime.Now - a.LastSeen).TotalSeconds < 15 ? "Thinking" : "Idle",
                        Calls = a.Calls,
                        Changes = a.Changes,
                    });
                }
                foreach (AgentStep s in Feed)
                {
                    v.Feed.Add(new StepView
                    {
                        Agent = Agents.TryGetValue(s.Agent, out AgentInfo a) ? a.Name : s.Agent,
                        Color = a?.Color ?? System.Drawing.Color.Gray,
                        Caption = s.Caption,
                        Error = s.Error,
                        Action = s.Action,
                        State = s.State,
                        Seconds = s.Seconds,
                        When = s.Started.AddSeconds(s.Seconds),
                        Changed = s.Created.Count + s.Modified.Count + s.ErasedCount,
                    });
                }
                return v;
            }
        }

        static void Refresh() => Entry.Main?.Post(RefreshNow);

        static void RefreshNow()
        {
            if (!Enabled) return;
            if (_palette == null || !_palette.Alive) ShowUi();
            else _palette.Show(Snapshot());
        }

        static void ShowOnModel(AgentInfo agent, AgentStep step)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !string.Equals(doc.Name, step.Document, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                if (_overlay == null) _overlay = new CursorOverlay();
                List<ObjectId> targets = TargetsOf(doc.Database, step);
                _overlay.Show(doc, agent.Name, agent.Color, step, targets);
                if (ZoomToWork && targets.Count > 0) _overlay.ZoomTo(doc, targets, step.ErasedBoxes);
            }
            catch (Exception ex) { Log.Error("agent cursors overlay: " + ex.Message); }
        }

        /// <summary>The entities a step worked on that still exist: created and changed first, then the ones it named.</summary>
        static List<ObjectId> TargetsOf(Database db, AgentStep step)
        {
            var ids = new List<ObjectId>();
            lock (Gate)
            {
                foreach (ObjectId id in step.Created.Concat(step.Modified))
                    if (!id.IsErased && id.Database == db && !ids.Contains(id)) ids.Add(id);
                foreach (string h in step.NamedHandles)
                    if (Acad.TryId(db, h, out ObjectId id) && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        // ---------------------------------------------------------------- bar buttons (main thread)

        static List<ObjectId> TouchedIds(Database db)
        {
            var ids = new List<ObjectId>();
            lock (Gate)
                foreach (string h in Touched)
                    if (Acad.TryId(db, h, out ObjectId id)) ids.Add(id);
            return ids;
        }

        public static void SelectTouched()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            List<ObjectId> ids = TouchedIds(doc.Database);
            if (ids.Count == 0) { Write("T3Lab Agent Cursors: nothing the AI changed is left in this drawing."); return; }
            using (doc.LockDocument())
            {
                try { doc.Editor.SetImpliedSelection(ids.ToArray()); }
                catch (Exception ex) { Write("T3Lab Agent Cursors: could not select (" + ex.Message + ")."); return; }
            }
            Write($"T3Lab Agent Cursors: {ids.Count} entities the AI changed are selected.");
        }

        public static void ZoomTouched()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            List<ObjectId> ids = TouchedIds(doc.Database);
            if (ids.Count == 0) { Write("T3Lab Agent Cursors: nothing the AI changed is left in this drawing."); return; }
            if (_overlay == null) _overlay = new CursorOverlay();
            _overlay.ZoomTo(doc, ids, new List<Extents3d>());
        }

        public static void Replay()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            List<Tuple<AgentInfo, AgentStep>> steps;
            lock (Gate)
                steps = Feed.Where(s => s.State == StepState.Ok && s.Action != ActionKind.Read &&
                                        string.Equals(s.Document, doc.Name, StringComparison.OrdinalIgnoreCase))
                    .Reverse().Select(s => Tuple.Create(Agents.TryGetValue(s.Agent, out AgentInfo a) ? a : null, s))
                    .Where(t => t.Item1 != null).ToList();
            if (steps.Count == 0) { Write("T3Lab Agent Cursors: no change to replay in this drawing."); return; }
            if (_overlay == null) _overlay = new CursorOverlay();
            _overlay.Replay(doc, steps.Select(t => Tuple.Create(t.Item1.Name, t.Item1.Color, t.Item2, TargetsOf(doc.Database, t.Item2))).ToList());
        }

        public static void ClearFeed()
        {
            lock (Gate)
            {
                Feed.Clear();
                Touched.Clear();
                foreach (AgentInfo a in Agents.Values) { a.Goal = null; a.Tasks.Clear(); a.Step = 0; a.Calls = 0; a.Changes = 0; }
            }
            _overlay?.Clear();
            RefreshNow();
        }

        static void Write(string message)
        {
            try { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + message + "\n"); }
            catch (Exception) { /* no editor */ }
        }

        // ---------------------------------------------------------------- t3lab_narrate

        public static void Register()
        {
            Registry.Add(NarrateTool, "Show goal and steps", ToolKind.Read,
                "Tell the user what you are doing in AutoCAD: your goal and task list appear on the T3Lab Agent Cursors bar, the current " +
                "task is ticked off as you go. Call it at the start of multi-step work (goal + tasks) and with step=N when you move to " +
                "task N; done=true at the end. Changes nothing in the drawing.",
                Obj(P("goal", Str("What you are doing, one short line")),
                    P("tasks", Strs("The steps you plan, short (max 8)")),
                    P("step", Int("The task you are on now, 1-based")),
                    P("done", Bool("All tasks are finished"))),
                NarrateHandler, needsDocument: false, tx: false, background: true);
        }

        static object NarrateHandler(ToolContext unused, Args a)
        {
            List<string> tasks = a.Has("tasks") ? a.Strs("tasks").Where(t => !string.IsNullOrWhiteSpace(t)).ToList() : null;
            Narrate(CurrentAgent, a.Str("goal"), tasks, a.Int("step", 0), a.Bool("done", false));
            var result = new Dictionary<string, object> { ["shown"] = Enabled };
            if (!Enabled) result["note"] = "Agent Cursors is off in AutoCAD; the user turns it on with T3CURSORS.";
            return result;
        }
    }
}
