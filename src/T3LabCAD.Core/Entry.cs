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
        public const string Version = "1.0.0";
        public const int PortMin = 48900, PortMax = 48908;

        internal static MainThread Main { get; private set; }
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
            Log.Info($"started v{Version} on 127.0.0.1:{server.Port}, {Registry.Count} tools, pid {Process.GetCurrentProcess().Id}");
        }

        public static void Stop()
        {
            if (_server == null) return;
            AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApp.DocumentManager.DocumentDestroyed -= OnDocumentDestroyed;
            _server.Stop();
            _server = null;
            Main?.Dispose();
            Main = null;
            Log.Info("stopped");
        }

        public static string Status()
        {
            if (_server == null) return $"T3LabCAD MCP v{Version}: stopped (T3MCPSTART to start)";
            string last = string.IsNullOrEmpty(LastCall) ? "none" : LastCall;
            return $"T3LabCAD MCP v{Version}: listening on 127.0.0.1:{_server.Port} | {Registry.Count} tools | {CallCount} calls | last: {last}";
        }

        internal static int Port => _server?.Port ?? 0;

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
