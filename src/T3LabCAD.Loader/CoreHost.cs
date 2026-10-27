using System;
using System.IO;
using System.Reflection;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace T3LabCAD.Loader
{
    /// <summary>
    /// Loads T3LabCAD.Core.dll from bytes (so the file is never locked and can be
    /// replaced while AutoCAD runs) and drives it through reflection:
    /// Entry.Start(string contentsDir, Action requestReload), Entry.Stop(), Entry.Status().
    /// </summary>
    internal static class CoreHost
    {
        const string CoreFile = "T3LabCAD.Core.dll";
        const string EntryTypeName = "T3LabCAD.Core.Entry";

        static Type _entry;
        static bool _reloadPending;

        internal static string ContentsDir => Path.GetDirectoryName(typeof(CoreHost).Assembly.Location);

        internal static void Start()
        {
            if (_entry == null) _entry = LoadCore();
            _entry.GetMethod("Start").Invoke(null, new object[] { ContentsDir, (Action)RequestReload });
            Write(Status());
        }

        internal static void Stop()
        {
            _entry?.GetMethod("Stop").Invoke(null, null);
        }

        internal static string Status()
        {
            return _entry == null ? "T3LabCAD MCP: not loaded" : (string)_entry.GetMethod("Status").Invoke(null, null);
        }

        internal static void Reload()
        {
            Stop();
            _entry = LoadCore();
            Start();
        }

        /// <summary>Called by Core (reload_addin tool) on the main thread: reload once AutoCAD is idle,
        /// after the tool's own response has gone out.</summary>
        static void RequestReload()
        {
            if (_reloadPending) return;
            _reloadPending = true;
            AcApp.Idle += OnIdleReload;
        }

        static void OnIdleReload(object sender, EventArgs e)
        {
            AcApp.Idle -= OnIdleReload;
            _reloadPending = false;
            try { Reload(); }
            catch (Exception ex) { Write("T3LabCAD: reload failed: " + Unwrap(ex).Message); }
        }

        static Type LoadCore()
        {
            string dll = Path.Combine(ContentsDir, CoreFile);
            if (!File.Exists(dll)) throw new FileNotFoundException("T3LabCAD.Core.dll is missing next to the loader.", dll);
            byte[] raw = File.ReadAllBytes(dll);
            string pdb = Path.ChangeExtension(dll, ".pdb");
            Assembly asm;
            try { asm = File.Exists(pdb) ? Assembly.Load(raw, File.ReadAllBytes(pdb)) : Assembly.Load(raw); }
            catch (BadImageFormatException) { throw; }
            catch { asm = Assembly.Load(raw); } // unreadable symbols: load without line numbers
            return asm.GetType(EntryTypeName, true);
        }

        internal static void Write(string message)
        {
            try { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + message + "\n"); }
            catch { /* no editor yet (startup) */ }
        }

        internal static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
