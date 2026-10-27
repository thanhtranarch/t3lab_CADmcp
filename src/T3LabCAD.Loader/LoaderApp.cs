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
            try { CoreHost.Start(); }
            catch (System.Exception ex) { CoreHost.Write("T3LabCAD: MCP server did not start: " + CoreHost.Unwrap(ex).Message); }

            try { T3Ribbon.Init(); }
            catch (System.Exception ex) { CoreHost.Write("T3LabCAD: ribbon tab not created: " + ex.Message); }
        }

        public void Terminate()
        {
            try { CoreHost.Stop(); }
            catch { /* AutoCAD is shutting down */ }
        }
    }

    public sealed class Commands
    {
        [CommandMethod("T3LAB", "T3MCPSTART", CommandFlags.Modal)]
        public void Start() => Guard(CoreHost.Start);

        [CommandMethod("T3LAB", "T3MCPSTOP", CommandFlags.Modal)]
        public void Stop() => Guard(() => { CoreHost.Stop(); CoreHost.Write(CoreHost.Status()); });

        [CommandMethod("T3LAB", "T3MCPSTATUS", CommandFlags.Modal)]
        public void Status() => CoreHost.Write(CoreHost.Status());

        /// <summary>Loads T3LabCAD.Core.dll again from the bundle folder (after a rebuild + install).</summary>
        [CommandMethod("T3LAB", "T3MCPRELOAD", CommandFlags.Modal)]
        public void Reload() => Guard(CoreHost.Reload);

        static void Guard(System.Action action)
        {
            try { action(); }
            catch (System.Exception ex) { CoreHost.Write("T3LabCAD: " + CoreHost.Unwrap(ex).Message); }
        }
    }
}
