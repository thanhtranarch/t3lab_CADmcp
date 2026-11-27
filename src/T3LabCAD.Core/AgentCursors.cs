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

    internal enum StepState { Running, Ok, Failed, Refused, Busy }

    /// <summary>One tool call as the Agent Cursors bar shows it.</summary>
    internal sealed class AgentStep
    {
        public string Agent, Tool, Caption, Error;
        public ActionKind Action;
        public DateTime Started = DateTime.Now;
        public double Seconds;
        public StepState State = StepState.Running;
        // handles only (never ObjectIds: a step outlives its drawing), resolved again in the drawing when shown
        public readonly List<string> NamedHandles = new List<string>();   // from the arguments and the result
        public readonly List<string> Created = new List<string>(), Modified = new List<string>(); // filled when the capture ends
        public readonly List<Extents3d> ErasedBoxes = new List<Extents3d>();
        public int ErasedCount;
        public string Document = "";                                        // full name of the drawing it ran in
        public int Changed => Created.Count + Modified.Count + ErasedCount;
        // the fair queue (Conductor): in line, then holding the slot, then running on AutoCAD's thread
        public volatile bool Queued, OnMain;
        public int Ahead;
        public string Behind;
        public DateTime Granted, RunStarted;
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
        public DateTime? LastEnd;
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
        public const string NarrateTool = "t3lab_narrate", ControlTool = "agent_cursors";
        const int FeedSize = 20, PlanMax = 8, TaskMax = 60, GoalMax = 90, MaxCapture = 200, MaxTouched = 5000;
        /// <summary>After a call, the agent is "thinking" (waiting for its model) this long, then idle.</summary>
        internal const double ThinkS = 20, BlockedAfterS = 2;

        static readonly object Gate = new object();
        static readonly Dictionary<string, AgentInfo> Agents = new Dictionary<string, AgentInfo>(StringComparer.OrdinalIgnoreCase);
        static readonly List<AgentStep> Feed = new List<AgentStep>();
        // handles the AI changed, per drawing (handles repeat across drawings)
        static readonly Dictionary<string, HashSet<string>> Touched = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        static ActivityPalette _palette;
        static CursorOverlay _overlay;
        static bool _hinted, _quitting, _hooked;

        public static volatile bool Enabled, Paused;
        public static volatile bool ShowOnDrawing, ZoomToWork;

        [ThreadStatic] public static string CurrentAgent;

        static string SettingsFile => Path.Combine(Log.DataDir, "agent_cursors.json");

        // ---------------------------------------------------------------- settings

        public static void Load()
        {
            try
            {
                ShowOnDrawing = true;   // on by default, as in T3Lab Revit MCP
                if (!File.Exists(SettingsFile)) return;
                var a = new Args(Json.Parse(File.ReadAllText(SettingsFile)));
                Enabled = a.Bool("enabled", false);
                ShowOnDrawing = a.Bool("show_on_drawing", true);
                ZoomToWork = a.Bool("zoom_to_work", false);
            }
            catch (Exception ex) { Log.Error("agent cursors settings: " + ex.Message); }
            Hook(true);
            if (Enabled) Entry.Main?.Post(() => ShowUi(true));
        }

        /// <summary>Drawing close and AutoCAD quit (main thread).</summary>
        static void Hook(bool on)
        {
            try
            {
                if (on == _hooked) return;
                _hooked = on;
                if (on)
                {
                    AcApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
                    AcApp.BeginQuit += OnBeginQuit;
                    AcApp.QuitAborted += OnQuitAborted;
                }
                else
                {
                    AcApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
                    AcApp.BeginQuit -= OnBeginQuit;
                    AcApp.QuitAborted -= OnQuitAborted;
                }
            }
            catch (Exception ex) { Log.Error("agent cursors events: " + ex.Message); }
        }

        /// <summary>A drawing closes: forget its handles, so nothing is matched in a later drawing of the same name.</summary>
        static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                string name = e.Document?.Name;
                if (string.IsNullOrEmpty(name)) return;
                lock (Gate)
                {
                    Touched.Remove(name);
                    foreach (AgentStep s in Feed.Where(x => string.Equals(x.Document, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        s.NamedHandles.Clear();
                        s.Created.Clear();
                        s.Modified.Clear();
                        s.ErasedBoxes.Clear();
                    }
                }
            }
            catch (Exception ex) { Log.Error("agent cursors close: " + ex.Message); }
        }

        /// <summary>The palette and the transients go while AutoCAD's window still exists (not in Terminate).</summary>
        static void OnBeginQuit(object sender, EventArgs e)
        {
            _quitting = true;
            HideUi();
        }

        static void OnQuitAborted(object sender, EventArgs e)
        {
            _quitting = false;
            if (Enabled) Entry.Main?.Post(() => ShowUi(true));
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

        /// <summary>T3CURSORS / ribbon: on; the bar again when it was closed; off. Main thread.</summary>
        public static string Toggle()
        {
            if (Enabled && (_palette == null || !_palette.Alive || !_palette.Visible))
            {
                ShowUi(true);
                return "T3Lab Agent Cursors: bar shown. Press Agent Cursors again to turn it off.";
            }
            SetEnabled(!Enabled);
            return Enabled ? "T3Lab Agent Cursors: on. The bar shows what each AI app does." : "T3Lab Agent Cursors: off.";
        }

        public static void SetEnabled(bool on)
        {
            Enabled = on;
            if (!on) Paused = false;
            Save();
            if (on) ShowUi(true);
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

        /// <summary>Update the bar; reveal = also open it if it is closed (otherwise a closed bar stays closed).</summary>
        static void ShowUi(bool reveal)
        {
            if (_quitting) return;
            try
            {
                bool created = false;
                if (_palette == null || !_palette.Alive) { _palette = new ActivityPalette(); created = true; }
                _palette.Show(Snapshot(), reveal || created);
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

        /// <summary>T3MCPSTOP, reload, AutoCAD exit (Terminate: the UI is already gone then, see OnBeginQuit).</summary>
        public static void Shutdown()
        {
            Hook(false);
            if (_quitting)
            {
                _palette = null;
                _overlay = null;
            }
            else HideUi();
            Paused = false;
            _hinted = false;
            lock (Gate)
            {
                Agents.Clear();
                Retired.Clear();
                Feed.Clear();
                Touched.Clear();
            }
        }

        // ---------------------------------------------------------------- agents

        /// <summary>A T3Style colour (ARGB) as a System.Drawing colour.</summary>
        internal static System.Drawing.Color C(uint argb) => System.Drawing.Color.FromArgb(unchecked((int)argb));

        /// <summary>Display name and colour of an AI app (T3Style.Agent, shared with T3Lab Revit MCP).</summary>
        static string Known(string rawName, out uint color) => T3Style.Agent(rawName, out color);

        /// <summary>
        /// One agent per chat: the key is "name#session" (the bridge's process id). A second chat of the same app is
        /// "Claude Code 2" in another colour, so two chats never share one pointer.
        /// </summary>
        static AgentInfo AgentFor(string clientKey)
        {
            string key = string.IsNullOrWhiteSpace(clientKey) ? "ai" : clientKey.Trim();
            if (Agents.TryGetValue(key, out AgentInfo known)) return known;
            string name = Known(BaseName(key), out uint color);
            List<AgentInfo> same = Agents.Values.Where(a => Known(BaseName(a.Key), out uint unused) == name).ToList();
            // a new chat inherits the name and colour of a chat of the same app that went quiet (bridges restart: names must
            // not climb to "7"); it starts with an empty plan, and the quiet one is retired, never shared by two live chats
            AgentInfo quiet = same.FirstOrDefault(a => a.Current == null && (DateTime.Now - a.LastSeen).TotalMinutes > QuietMinutes);
            AgentInfo info;
            if (quiet != null)
            {
                Agents.Remove(quiet.Key);
                Retired[quiet.Key] = quiet;
                info = new AgentInfo { Key = key, Name = quiet.Name, Color = quiet.Color };
            }
            else
            {
                int n = 1;
                string label = name;
                while (same.Any(a => a.Name == label)) label = name + " " + ++n;
                info = new AgentInfo { Key = key, Name = label, Color = C(n == 1 ? color : T3Style.Extra(name, n)) };
            }
            Agents[key] = info;
            return info;
        }

        static IEnumerable<AgentInfo> AllAgents() => Agents.Values;

        /// <summary>Agents a newer chat replaced: their steps in the feed keep their name and colour. A retired chat that comes back is a new agent.</summary>
        static readonly Dictionary<string, AgentInfo> Retired = new Dictionary<string, AgentInfo>(StringComparer.OrdinalIgnoreCase);

        const double QuietMinutes = 5;

        static string BaseName(string key)
        {
            int hash = key.LastIndexOf('#');
            return hash > 0 ? key.Substring(0, hash) : key;
        }

        /// <summary>Who an agent is, for messages (no registration): "Claude Code".</summary>
        public static string Label(string clientKey) => Known(BaseName(clientKey ?? "ai"), out uint unused);

        /// <summary>"claude-code#4812" from the bridge's client {name, version, session}.</summary>
        public static string ClientKey(object raw)
        {
            if (raw is Dictionary<string, object> d)
            {
                var a = new Args(d);
                string name = a.Str("name");
                string session = a.Str("session");
                return string.IsNullOrEmpty(session) ? name : (name ?? "ai") + "#" + session;
            }
            return raw as string;
        }

        // ---------------------------------------------------------------- calls

        /// <summary>A call is starting (socket thread). Returns the step to pass to End, or null when off.</summary>
        public static AgentStep Begin(ToolDef def, Args args, string client)
        {
            if (!Enabled)
            {
                if (!_hinted && def.Name != NarrateTool && def.Name != ControlTool)
                {
                    _hinted = true;
                    string who = AgentForName(client);
                    Entry.Main?.Post(() => Write($"T3Lab: {who} is working in AutoCAD through MCP. {HowToOpen()} to see what it does (Agent Cursors)."));
                }
                return null;
            }
            if (def.Name == NarrateTool || def.Name == ControlTool) return null;
            var step = new AgentStep
            {
                Tool = def.Name,
                Action = ActionOf(def, args),
                Caption = Describe(def, args),
                Document = Entry.ActiveDocument,
            };
            AddCapped(step.NamedHandles, HandlesIn(args.Raw("handles")));
            AddCapped(step.NamedHandles, HandlesIn(args.Raw("handle")));
            AgentInfo agent;
            lock (Gate)
            {
                agent = AgentFor(client);
                step.Agent = agent.Key;
                agent.Current = step;
                agent.LastSeen = DateTime.Now;
                agent.Calls++;
            }
            Refresh();
            if (ShowOnDrawing) PointerState(agent.Name, CursorOverlay.Mode.Working, step.Caption + "…");
            return step;
        }

        /// <summary>The call has to wait for its turn (Conductor, socket thread).</summary>
        public static void Queued(AgentStep step, int ahead, string runningClient)
        {
            if (step == null) return;
            step.Ahead = ahead;
            step.Behind = runningClient == null ? null : NameOf(runningClient);
            step.Queued = true;
            Refresh();
        }

        /// <summary>The call holds AutoCAD's slot; AutoCAD may still be blocked by a modal dialog until OnMain.</summary>
        public static void Granted(AgentStep step)
        {
            if (step == null) return;
            step.Granted = DateTime.Now;
            bool was = step.Queued;
            step.Queued = false;
            if (was) Refresh();
        }

        /// <summary>The drawing a call works in (main thread, once resolved); another than the active one shows on its caption.</summary>
        public static void SetDocument(AgentStep step, string name, bool active)
        {
            if (step == null || string.IsNullOrEmpty(name)) return;
            step.Document = name;
            if (!active)
            {
                step.Caption += " · in " + System.IO.Path.GetFileName(name);
                Refresh();
            }
        }

        public static void OnMain(AgentStep step)
        {
            if (step == null) return;
            step.RunStarted = DateTime.Now;
            step.OnMain = true;
        }

        static string NameOf(string clientKey)
        {
            lock (Gate) return Agents.TryGetValue(clientKey, out AgentInfo a) ? a.Name : Label(clientKey);
        }

        /// <summary>Retag an agent's pointer that is on the drawing (any thread; never creates one).</summary>
        static void PointerState(string agent, CursorOverlay.Mode mode, string text) =>
            Entry.Main?.Post(() =>
            {
                try { _overlay?.SetState(agent, mode, text); }
                catch (Exception ex) { Log.Error("agent cursors tag: " + ex.Message); }
            });

        static string AgentForName(string client)
        {
            lock (Gate) return AgentFor(client).Name;
        }

        static void AddCapped(List<string> into, IEnumerable<string> handles)
        {
            foreach (string h in handles)
            {
                if (into.Count >= MaxCapture) return;
                into.Add(h);
            }
        }

        static string _howToOpen;

        /// <summary>"Type T3CURSORS", or "Restart AutoCAD, then type T3CURSORS" when the loaded Loader predates the command.</summary>
        static string HowToOpen()
        {
            if (_howToOpen != null) return _howToOpen;
            bool has = true;
            try
            {
                System.Reflection.Assembly loader = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(asm => asm.GetName().Name == "T3LabCAD.Loader");
                if (loader != null) has = loader.GetType("T3LabCAD.Loader.Commands")?.GetMethod("Cursors") != null;
            }
            catch (Exception) { /* assume a current Loader */ }
            return _howToOpen = has ? "Type T3CURSORS" : "Restart AutoCAD (new T3Lab MCP ribbon panel), then type T3CURSORS";
        }

        public const string PausedMessage =
            "The user paused AI changes in AutoCAD (T3Lab Agent Cursors bar). Read-only tools still work; ask the user to press " +
            "Resume on the bar (T3CURSORS shows it), then try again.";

        /// <summary>
        /// While paused: refuse anything that changes the drawing or writes a file. Reads, views (zoom, selection, UCS,
        /// layouts), dry runs and cancel_command still run. Never throws.
        /// </summary>
        public static bool Blocks(ToolDef def, Args args)
        {
            try
            {
                if (!Enabled || !Paused || def.Name == NarrateTool || def.Name == ControlTool || def.Name == "cancel_command") return false;
                ActionKind k = ActionOf(def, args);
                if (k == ActionKind.Read || k == ActionKind.View) return WritesFileNow(args);
                if (def.Kind == ToolKind.Read) return def.WritesFiles; // list_drawing_files reads; export_entities writes
                return true;
            }
            catch (Exception) { return def.Kind != ToolKind.Read; }
        }

        static bool WritesFileNow(Args args) =>
            !string.IsNullOrEmpty(args.Str("csv_path")) || !string.IsNullOrEmpty(args.Str("save_path"));

        /// <summary>The calls whose drawing changes are captured: writes, and run_command (not open / close / switch drawing).</summary>
        public static bool Captures(ToolDef def) => def.Kind == ToolKind.Write || def.Name == "run_command";

        /// <summary>A call ended (socket thread): record it, then show it.</summary>
        public static void End(AgentStep step, bool ok, bool refused, bool busy, string error, object result)
        {
            if (step == null) return;
            step.Seconds = (DateTime.Now - step.Started).TotalSeconds;
            step.State = busy ? StepState.Busy : refused ? StepState.Refused : ok ? StepState.Ok : StepState.Failed;
            step.Queued = false;
            step.Error = error;
            if (result != null && ok) CollectResultHandles(result, step.NamedHandles, 0);
            AgentInfo agent;
            lock (Gate)
            {
                agent = Agents.TryGetValue(step.Agent, out AgentInfo a) ? a : null;
                if (agent != null)
                {
                    if (agent.Current == step) agent.Current = null;
                    agent.LastSeen = DateTime.Now;
                    agent.LastEnd = DateTime.Now;
                    agent.Changes += step.Changed;
                }
                Feed.Insert(0, step);
                if (Feed.Count > FeedSize) Feed.RemoveAt(Feed.Count - 1);
                if (step.State == StepState.Ok && !string.IsNullOrEmpty(step.Document))
                {
                    if (!Touched.TryGetValue(step.Document, out HashSet<string> set))
                        Touched[step.Document] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    IEnumerable<string> touched = step.Created.Concat(step.Modified);
                    if (step.Action != ActionKind.Read && step.Action != ActionKind.View) touched = touched.Concat(step.NamedHandles);
                    foreach (string h in touched)
                        if (set.Count < MaxTouched) set.Add(h);
                }
            }
            Entry.Main?.Post(() =>
            {
                RefreshNow();
                if (!ShowOnDrawing || agent == null) return;
                if (step.State == StepState.Ok) ShowOnModel(agent, step);
                else _overlay?.SetState(agent.Name, CursorOverlay.Mode.Done,
                    step.State == StepState.Busy ? "waiting for AutoCAD" : step.State == StepState.Refused ? "paused" : "failed");
            });
        }

        /// <summary>The goal / task banner (t3lab_narrate). Any thread.</summary>
        public static void Narrate(string client, string goal, List<string> tasks, int step, bool done)
        {
            string name;
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
                name = agent.Name;
            }
            Refresh();
            if (done && Enabled && ShowOnDrawing) PointerState(name, CursorOverlay.Mode.Finished, "done");
        }

        static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        // ---------------------------------------------------------------- change capture

        /// <summary>
        /// Entities appended / changed / erased in the drawing while a writing call runs (also by run_command). Main thread.
        /// The event handlers only note ObjectIds; they become handles when the capture ends, or earlier if the drawing closes.
        /// </summary>
        public sealed class Capture : IDisposable
        {
            readonly AgentStep _step;
            readonly Database _db;
            readonly HashSet<ObjectId> _seen = new HashSet<ObjectId>();
            readonly List<ObjectId> _created = new List<ObjectId>(), _modified = new List<ObjectId>();
            readonly List<Extents3d> _erasedBoxes = new List<Extents3d>();
            int _erased;
            bool _open;

            public Capture(AgentStep step, Database db)
            {
                _step = step;
                _db = db;
                db.ObjectAppended += OnAppended;
                db.ObjectModified += OnModified;
                db.ObjectErased += OnErased;
                AcApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
                _open = true;
            }

            bool Room() => _created.Count + _modified.Count < MaxCapture;

            void OnAppended(object sender, ObjectEventArgs e)
            {
                if (e.DBObject is Entity && Room() && _seen.Add(e.DBObject.ObjectId)) _created.Add(e.DBObject.ObjectId);
            }

            void OnModified(object sender, ObjectEventArgs e)
            {
                if (!(e.DBObject is Entity) || e.DBObject is AttributeReference) return;
                if (Room() && _seen.Add(e.DBObject.ObjectId)) _modified.Add(e.DBObject.ObjectId);
            }

            void OnErased(object sender, ObjectErasedEventArgs e)
            {
                if (!e.Erased || !(e.DBObject is Entity ent)) return;
                _erased++;
                if (_seen.Contains(ent.ObjectId))
                {
                    _created.Remove(ent.ObjectId);
                    _modified.Remove(ent.ObjectId);
                }
                if (_erasedBoxes.Count < 12)
                {
                    try
                    {
                        Extents3d? b = ent.Bounds;
                        if (b.HasValue) _erasedBoxes.Add(b.Value);
                    }
                    catch (Exception) { /* no extents for this one */ }
                }
            }

            /// <summary>run_command "_.CLOSE" and the like: finish while the drawing still exists.</summary>
            void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
            {
                try
                {
                    if (e.Document != null && e.Document.Database == _db) Finish();
                }
                catch (Exception) { /* never fail the close */ }
            }

            public void Dispose() => Finish();

            void Finish()
            {
                if (!_open) return;
                _open = false;
                try { AcApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed; }
                catch (Exception) { /* AutoCAD is closing */ }
                if (_db.IsDisposed) return; // gone without the event: its ObjectIds must not be touched
                var created = new List<string>();
                var modified = new List<string>();
                try
                {
                    _db.ObjectAppended -= OnAppended;
                    _db.ObjectModified -= OnModified;
                    _db.ObjectErased -= OnErased;
                    foreach (ObjectId id in _created) if (id.IsValid && !id.IsErased) created.Add(id.Handle.ToString());
                    foreach (ObjectId id in _modified) if (id.IsValid && !id.IsErased) modified.Add(id.Handle.ToString());
                }
                catch (Exception ex) { Log.Error("agent cursors capture: " + ex.Message); }
                lock (Gate)
                {
                    _step.Created.AddRange(created);
                    _step.Modified.AddRange(modified);
                    _step.ErasedBoxes.AddRange(_erasedBoxes);
                    _step.ErasedCount += _erased;
                }
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
            if (depth > 4 || into.Count >= MaxCapture) return;
            switch (value)
            {
                case Dictionary<string, object> d:
                    foreach (var kv in d)
                    {
                        if (HandleKeys.Contains(kv.Key)) AddCapped(into, HandlesIn(kv.Value));
                        else if (kv.Value is IDictionary || (kv.Value is IList && !(kv.Value is string))) CollectResultHandles(kv.Value, into, depth + 1);
                    }
                    break;
                case IList list:
                    foreach (object o in list)
                    {
                        if (into.Count >= MaxCapture) return;
                        CollectResultHandles(o, into, depth + 1);
                    }
                    break;
            }
        }

        // ---------------------------------------------------------------- UI glue (main thread)

        internal enum AgentState { Idle, Thinking, Queued, Blocked, Working, Paused }

        internal sealed class AgentView
        {
            public string Name, Status, Goal, Now, Behind;
            public System.Drawing.Color Color;
            public List<string> Tasks;
            public int Step, Ahead;
            public bool PlanDone, Working;
            public AgentState State;
            public ActionKind NowAction;
            public double NowSeconds;
            public DateTime? LastEnd;
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
                string active = Entry.ActiveDocument ?? "";
                var v = new View
                {
                    Paused = Paused, ShowOnDrawing = ShowOnDrawing, ZoomToWork = ZoomToWork,
                    Touched = Touched.TryGetValue(active, out HashSet<string> set) ? set.Count : 0,
                };
                foreach (AgentInfo a in AllAgents().OrderByDescending(x => x.LastSeen))
                {
                    AgentStep cur = a.Current;
                    AgentState state = StateOf(a, cur);
                    DateTime since = cur == null ? DateTime.Now : cur.OnMain ? cur.RunStarted : cur.Granted != default ? cur.Granted : cur.Started;
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
                        NowSeconds = cur == null ? 0 : (DateTime.Now - since).TotalSeconds,
                        State = state,
                        Status = StatusText(state, cur),
                        Ahead = cur?.Ahead ?? 0,
                        Behind = cur?.Behind,
                        LastEnd = a.LastEnd,
                        Calls = a.Calls,
                        Changes = a.Changes,
                    });
                }
                foreach (AgentStep s in Feed)
                {
                    AgentInfo a = Agents.TryGetValue(s.Agent, out AgentInfo live) ? live : Retired.TryGetValue(s.Agent, out AgentInfo old) ? old : null;
                    v.Feed.Add(new StepView
                    {
                        Agent = a?.Name ?? Label(s.Agent),
                        Color = a?.Color ?? C(T3Style.Action("Read")),
                        Caption = s.Caption,
                        Error = s.Error,
                        Action = s.Action,
                        State = s.State,
                        Seconds = s.Seconds,
                        When = s.Started.AddSeconds(s.Seconds),
                        Changed = s.Changed,
                    });
                }
                return v;
            }
        }

        static AgentState StateOf(AgentInfo a, AgentStep cur)
        {
            if (Paused) return AgentState.Paused;
            if (cur != null)
            {
                if (cur.Queued) return AgentState.Queued;
                bool waits = !cur.OnMain && cur.Granted != default && (DateTime.Now - cur.Granted).TotalSeconds > BlockedAfterS;
                return waits ? AgentState.Blocked : AgentState.Working;
            }
            if (!a.PlanDone && a.LastEnd.HasValue && (DateTime.Now - a.LastEnd.Value).TotalSeconds < ThinkS) return AgentState.Thinking;
            return AgentState.Idle;
        }

        static string StatusText(AgentState state, AgentStep cur)
        {
            switch (state)
            {
                case AgentState.Paused: return "Paused";
                case AgentState.Queued: return cur.Ahead > 0 ? $"In line · {cur.Ahead} ahead" : "In line · next";
                case AgentState.Blocked: return "Waiting for AutoCAD";
                case AgentState.Working: return "Working";
                case AgentState.Thinking: return "Thinking";
                default: return "Idle";
            }
        }

        static void Refresh() => Entry.Main?.Post(RefreshNow);

        static void RefreshNow()
        {
            if (!Enabled || _quitting) return;
            ShowUi(false);
        }

        static void ShowOnModel(AgentInfo agent, AgentStep step)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !string.Equals(doc.Name, step.Document, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                // reads and view changes only move the pointer: no frames, no zoom
                bool change = step.Action != ActionKind.Read && step.Action != ActionKind.View;
                if (_overlay == null) _overlay = new CursorOverlay();
                List<ObjectId> targets = TargetsOf(doc.Database, step, change ? MaxCapture : 1);
                _overlay.Show(doc, agent.Name, agent.Color, step, targets, change);
                if (ZoomToWork && change && (targets.Count > 0 || step.ErasedBoxes.Count > 0)) _overlay.ZoomTo(doc, targets, step.ErasedBoxes);
            }
            catch (Exception ex) { Log.Error("agent cursors overlay: " + ex.Message); }
        }

        /// <summary>
        /// Entities of this drawing's current space, from these handles: the ones that still exist, at most max
        /// (a change inside a block definition or another layout has no place to show here).
        /// </summary>
        static List<ObjectId> ResolveInSpace(Database db, IEnumerable<string> handles, int max)
        {
            var ids = new List<ObjectId>();
            var seen = new HashSet<ObjectId>();
            ObjectId space = db.CurrentSpaceId;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (string h in handles)
                {
                    if (ids.Count >= max) break;
                    if (!Acad.TryId(db, h, out ObjectId id) || !seen.Add(id)) continue;
                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is Entity ent && ent.OwnerId == space) ids.Add(id);
                    }
                    catch (Exception) { /* not readable: skip */ }
                }
                tr.Commit();
            }
            return ids;
        }

        /// <summary>The entities a step worked on: created and changed first, then the ones it named.</summary>
        static List<ObjectId> TargetsOf(Database db, AgentStep step, int max)
        {
            List<string> handles;
            lock (Gate) handles = step.Created.Concat(step.Modified).Concat(step.NamedHandles).ToList();
            return ResolveInSpace(db, handles, max);
        }

        // ---------------------------------------------------------------- bar buttons (main thread)

        static List<ObjectId> TouchedIds(Document doc)
        {
            List<string> handles;
            lock (Gate) handles = Touched.TryGetValue(doc.Name, out HashSet<string> set) ? set.ToList() : new List<string>();
            return ResolveInSpace(doc.Database, handles, MaxTouched);
        }

        public static void SelectTouched()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            List<ObjectId> ids = TouchedIds(doc);
            if (ids.Count == 0) { Write("T3Lab Agent Cursors: nothing the AI changed is left in this space of the drawing."); return; }
            using (doc.LockDocument())
            {
                try { doc.Editor.SetImpliedSelection(ids.ToArray()); }
                catch (Exception ex) { Write("T3Lab Agent Cursors: could not select (" + ex.Message + ")."); return; }
            }
            if (_overlay == null) _overlay = new CursorOverlay();
            _overlay.ZoomTo(doc, ids, new List<Extents3d>());
            Write($"T3Lab Agent Cursors: {ids.Count} entities the AI changed are selected.");
        }

        public static void ZoomTouched()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            List<ObjectId> ids = TouchedIds(doc);
            if (ids.Count == 0) { Write("T3Lab Agent Cursors: nothing the AI changed is left in this space of the drawing."); return; }
            if (_overlay == null) _overlay = new CursorOverlay();
            _overlay.ZoomTo(doc, ids, new List<Extents3d>());
        }

        public static void Replay()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            List<Tuple<AgentInfo, AgentStep>> steps;
            lock (Gate)
                steps = Feed.Where(s => s.State == StepState.Ok && s.Action != ActionKind.Read && s.Action != ActionKind.View &&
                                        string.Equals(s.Document, doc.Name, StringComparison.OrdinalIgnoreCase))
                    .Reverse().Select(s => Tuple.Create(Agents.TryGetValue(s.Agent, out AgentInfo a) ? a : Retired.TryGetValue(s.Agent, out AgentInfo r) ? r : null, s))
                    .Where(t => t.Item1 != null).ToList();
            if (steps.Count == 0) { Write("T3Lab Agent Cursors: no change to replay in this drawing."); return; }   // same words as Revit's toast
            if (_overlay == null) _overlay = new CursorOverlay();
            _overlay.Replay(doc, steps.Select(t => Tuple.Create(t.Item1.Name, t.Item1.Color, t.Item2, TargetsOf(doc.Database, t.Item2, MaxCapture))).ToList());
        }

        public static void ClearFeed()
        {
            lock (Gate)
            {
                Feed.Clear();
                Touched.Clear();
                Retired.Clear();
                foreach (AgentInfo a in AllAgents()) { a.Goal = null; a.Tasks.Clear(); a.Step = 0; a.Calls = 0; a.Changes = 0; }
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

            // main thread (not background): turning it on builds the palette
            Registry.Add(ControlTool, "Agent Cursors", ToolKind.App,
                "The T3Lab Agent Cursors bar that shows the user what AI apps do in AutoCAD. action=status (default): on or off, paused, " +
                "options and each agent with its state. on: turn it on and show the bar. pause: refuse changes from every AI app until the " +
                "user presses Resume. Only the user turns it off or resumes, on the bar or the ribbon.",
                Obj(P("action", Enum("What to do (default status)", "status", "on", "pause"))),
                ControlHandler, needsDocument: false, tx: false, duringCommand: true);
        }

        static object ControlHandler(ToolContext unused, Args a)
        {
            string action = (a.Str("action") ?? "status").Trim().ToLowerInvariant();
            switch (action)
            {
                case "status": break;
                case "on":
                    if (!Enabled) SetEnabled(true);
                    else ShowUi(true);
                    break;
                case "pause":
                    if (!Enabled) SetEnabled(true);
                    SetPaused(true);
                    break;
                case "off":
                case "resume":
                    // the bar is the user's oversight: an AI app never hides its own work or lifts the user's pause
                    throw new ToolError("Only the user turns Agent Cursors off or resumes AI changes, on the bar or the ribbon.");
                default:
                    throw new ToolError("action: status, on or pause.");
            }
            View v = Snapshot();
            return new Dictionary<string, object>
            {
                ["enabled"] = Enabled,
                ["paused"] = v.Paused,
                ["show_on_drawing"] = v.ShowOnDrawing,
                ["follow"] = v.ZoomToWork,
                ["agents"] = v.Agents.Select(x => (object)new Dictionary<string, object>
                {
                    ["name"] = x.Name, ["state"] = x.Status, ["calls"] = x.Calls, ["changes"] = x.Changes, ["goal"] = x.Goal,
                }).ToList(),
            };
        }


        static object NarrateHandler(ToolContext unused, Args a)
        {
            List<string> tasks = a.Has("tasks") ? a.Strs("tasks").Where(t => !string.IsNullOrWhiteSpace(t)).ToList() : null;
            Narrate(CurrentAgent, a.Str("goal"), tasks, a.Int("step", 0), a.Bool("done", false));
            var result = new Dictionary<string, object> { ["shown"] = Enabled };
            if (!Enabled) result["note"] = "Agent Cursors is off in AutoCAD; the user turns it on with T3CURSORS (ribbon Add-ins > T3Lab MCP).";
            return result;
        }
    }
}
