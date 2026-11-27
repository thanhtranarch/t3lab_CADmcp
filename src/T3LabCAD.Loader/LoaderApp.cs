using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(T3LabCAD.Loader.LoaderApp))]
[assembly: CommandClass(typeof(T3LabCAD.Loader.Commands))]

namespace T3LabCAD.Loader
{
    /// <summary>Entry point AutoCAD calls when the bundle (or NETLOAD) loads this DLL.</summary>
    public sealed class LoaderApp : IExtensionApplication
    {
        public void Initialize()
        {
            if (Settings.AutoStart)
            {
                try { CoreHost.Start(); }
                catch (System.Exception ex) { CoreHost.Write("T3Lab CAD MCP: the server did not start: " + CoreHost.Unwrap(ex).Message); }
            }
            else CoreHost.Write("T3Lab CAD MCP is stopped (stopped last time). Start / Stop on the Add-ins tab, or T3MCP, starts it.");

            try { T3Ribbon.Init(); }
            catch (System.Exception ex) { CoreHost.Write("T3Lab CAD MCP: ribbon panel not created: " + ex.Message); }
        }

        public void Terminate()
        {
            try { CoreHost.Stop(); }
            catch { /* AutoCAD is shutting down */ }
        }
    }

    /// <summary>
    /// The same flow as T3Lab Revit MCP's ribbon: Start / Stop toggles the server and a stopped server stays stopped at the
    /// next AutoCAD start; Agent Cursors starts the server when needed; Connect AI adds AutoCAD to every AI app found.
    /// </summary>
    public sealed class Commands
    {
        const string Stopped = "T3Lab CAD MCP: server stopped. AI apps can no longer reach this AutoCAD.\n" +
                               "It stays off at the next AutoCAD start until you press Start again.";

        /// <summary>Start / Stop (ribbon).</summary>
        [CommandMethod("T3LAB", "T3MCP", CommandFlags.Modal)]
        public void StartStop() => Guard(() =>
        {
            if (CoreHost.Running) Stop();
            else Start();
        });

        [CommandMethod("T3LAB", "T3MCPSTART", CommandFlags.Modal)]
        public void Start() => Guard(() =>
        {
            Settings.AutoStart = true;
            CoreHost.Start();
        });

        [CommandMethod("T3LAB", "T3MCPSTOP", CommandFlags.Modal)]
        public void Stop() => Guard(() =>
        {
            CoreHost.Stop();
            Settings.AutoStart = false;
            CoreHost.Write(Stopped);
        });

        /// <summary>Connect AI (ribbon): the AI apps found on this PC get the t3lab-autocad server.</summary>
        [CommandMethod("T3LAB", "T3MCPCONNECT", CommandFlags.Modal)]
        public void ConnectAi() => Guard(Connect.Run);

        [CommandMethod("T3LAB", "T3MCPSTATUS", CommandFlags.Modal)]
        public void Status() => CoreHost.Write(CoreHost.Status());

        /// <summary>Loads T3LabCAD.Core.dll again from the bundle folder (after a rebuild + install).</summary>
        [CommandMethod("T3LAB", "T3MCPRELOAD", CommandFlags.Modal)]
        public void Reload() => Guard(CoreHost.Reload);

        /// <summary>Agent Cursors on / off: the bar (and, if chosen, the pointers on the drawing) that show what AI apps do.</summary>
        [CommandMethod("T3LAB", "T3CURSORS", CommandFlags.Modal)]
        public void Cursors() => Guard(() =>
        {
            if (!CoreHost.Running) CoreHost.Start();   // as in Revit: the bar needs the server
            CoreHost.Write(CoreHost.ToggleAgentCursors());
        });

        static void Guard(System.Action action)
        {
            try { action(); }
            catch (System.Exception ex) { CoreHost.Write("T3Lab CAD MCP: " + CoreHost.Unwrap(ex).Message); }
        }
    }

    /// <summary>%APPDATA%\T3LabCAD\autostart.off keeps the server off at AutoCAD start (Start / Stop), as in T3Lab Revit MCP.</summary>
    internal static class Settings
    {
        internal static string DataDir => System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "T3LabCAD");
        static string OffFlag => System.IO.Path.Combine(DataDir, "autostart.off");

        internal static bool AutoStart
        {
            get => !System.IO.File.Exists(OffFlag);
            set
            {
                try
                {
                    if (value) { if (System.IO.File.Exists(OffFlag)) System.IO.File.Delete(OffFlag); }
                    else
                    {
                        System.IO.Directory.CreateDirectory(DataDir);
                        System.IO.File.WriteAllText(OffFlag, "T3Lab CAD MCP does not start with AutoCAD while this file exists.");
                    }
                }
                catch (System.Exception) { /* the flag is a convenience */ }
            }
        }
    }
}
