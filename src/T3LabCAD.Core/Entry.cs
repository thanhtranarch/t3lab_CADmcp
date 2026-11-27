using System;
using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace T3LabCAD.Core
{
    /// <summary>
    /// Called by T3LabCAD.Loader through reflection, always on the AutoCAD main thread.
    /// Start() picks the first free port in PortMin..PortMax (one per AutoCAD process),
    /// the same range bridge/t3lab_cad_bridge.py scans.
    /// </summary>
    public static class Entry
    {
        public const string Version = "1.5.0";
        public const int PortMin = 48900, PortMax = 48908;

        internal static MainThread Main { get; private set; }
        /// <summary>The one AutoCAD slot, shared fairly by every AI app (Conductor.cs).</summary>
        internal static readonly Conductor Queue = new Conductor();
        internal static string ContentsDir { get; private set; }
        internal static string AcadVersion { get; private set; } = "";
        internal static volatile string ActiveDocument = "";
        internal static volatile string LastCall = "";
        internal static long CallCount;

        static Server _server;
        static Action _requestReload;

        public static void Start(string contentsDir, Action requestReload)
        {
            if (_server != null) return;
            ContentsDir = contentsDir;
            _requestReload = requestReload;
            AcadVersion = Convert.ToString(AcApp.GetSystemVariable("ACADVER"));
            ActiveDocument = AcApp.DocumentManager.MdiActiveDocument?.Name ?? "";

            Registry.Build();
            Main = new MainThread();
            var server = new Server(Rpc.Handle);
            try { server.Start(PortMin, PortMax); }
            catch
            {
                Main.Dispose();
                Main = null;
                throw;
            }
            _server = server;
            AcApp.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApp.DocumentManager.DocumentDestroyed += OnDocumentDestroyed;
            AgentCursors.Load();
            Log.Info($"started v{Version} on 127.0.0.1:{server.Port}, {Registry.Count} tools, pid {Process.GetCurrentProcess().Id}");
        }

        public static void Stop()
        {
            if (_server == null) return;
            AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApp.DocumentManager.DocumentDestroyed -= OnDocumentDestroyed;
            try { AgentCursors.Shutdown(); }
            catch (Exception ex) { Log.Error("agent cursors: " + ex.Message); }
            _server.Stop();
            _server = null;
            Main?.Dispose();
            Main = null;
            Log.Info("stopped");
        }

        public static bool Running => _server != null;

        /// <summary>Same lines as T3Lab Revit MCP's Status.</summary>
        public static string Status()
        {
            if (_server == null) return $"T3Lab CAD MCP v{Version}: stopped (Start / Stop on the Add-ins tab, or T3MCP, starts it).";
            string last = string.IsNullOrEmpty(LastCall) ? "none" : LastCall;
            var q = Queue.Status();
            string queue = (bool)q["busy"] ? $"busy ({q["tool"]}), {q["waiting"]} waiting" : "free";
            return $"T3Lab CAD MCP v{Version}: listening on 127.0.0.1:{_server.Port}\n{Registry.Count} tools · {CallCount} calls · last: {last}\n" +
                   $"Queue: {queue}\nAgent Cursors: {(AgentCursors.Enabled ? "on" : "off")}\nLog: {Log.FilePath}";
        }

        internal static int Port => _server?.Port ?? 0;

        /// <summary>T3CURSORS (Loader command / ribbon): turn Agent Cursors on or off. Main thread; returns the message to show.</summary>
        public static string ToggleAgentCursors()
        {
            if (_server == null) return "T3Lab CAD MCP is stopped (press Start first).";
            return AgentCursors.Toggle();
        }

        internal static bool RequestReload()
        {
            if (_requestReload == null) return false;
            _requestReload();
            return true;
        }

        static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            ActiveDocument = e.Document?.Name ?? "";
        }

        static void OnDocumentDestroyed(object sender, DocumentDestroyedEventArgs e)
        {
            if (string.Equals(ActiveDocument, e.FileName, StringComparison.OrdinalIgnoreCase)) ActiveDocument = "";
        }
    }
}
