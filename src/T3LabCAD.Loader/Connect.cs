using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace T3LabCAD.Loader
{
    /// <summary>
    /// Connect AI (T3MCPCONNECT, ribbon): runs %APPDATA%\T3LabCAD\register_mcp.py in a console the user can read, for every AI
    /// app found (Claude Desktop, Claude Code, Codex, Cursor, VS Code, Gemini CLI, Windsurf, Antigravity) or as a check that
    /// changes nothing. Same script, clients and choices as T3Lab Revit MCP's Connect AI button.
    /// </summary>
    internal static class Connect
    {
        internal static void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ask = new PromptKeywordOptions(
                "\nConnect AI apps to this AutoCAD (Claude Desktop, Claude Code, Codex, Cursor, VS Code, Gemini CLI, Windsurf, Antigravity)")
            {
                AllowNone = true,
            };
            ask.Keywords.Add("Connect");
            ask.Keywords.Add("Check");
            ask.Keywords.Default = "Connect";
            PromptResult answer = doc.Editor.GetKeywords(ask);
            if (answer.Status != PromptStatus.OK && answer.Status != PromptStatus.None) return;
            bool check = answer.StringResult == "Check";

            string bridge = Path.Combine(Settings.DataDir, "bridge.py"), script = Path.Combine(Settings.DataDir, "register_mcp.py");
            string missing = !File.Exists(bridge) ? bridge : !File.Exists(script) ? script : null;
            if (missing != null)
            {
                CoreHost.Write("T3Lab CAD MCP: " + missing + " is missing. Connect again from T3Lab MCP Control, or run scripts/install.ps1.");
                return;
            }
            string python = FindPython();
            if (python == null)
            {
                CoreHost.Write("T3Lab CAD MCP: no Python 3 found. Install Python 3 (python.org or the Microsoft Store), then try again.");
                return;
            }
            string args = $"-I \"{script}\" --bridge \"{bridge}\" --python \"{python}\"" + (check ? " --check" : "");
            try
            {
                // cmd /k keeps the window open so the user reads what was connected
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{python}\" {args}\"") { UseShellExecute = true });
                CoreHost.Write(check
                    ? "T3Lab CAD MCP: checking the AI apps in a new window (nothing is changed)."
                    : "T3Lab CAD MCP: connecting the AI apps in a new window. Restart each AI app afterwards.");
            }
            catch (Exception ex) { CoreHost.Write("T3Lab CAD MCP: could not open the console: " + ex.Message); }
        }

        /// <summary>A real python.exe (the client configs keep its path): PATH, per-user / all-users installs, the py launcher, the Store.</summary>
        static string FindPython()
        {
            var candidates = new List<string>();
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string d = dir.Trim().Trim('"');
                // the WindowsApps python.exe is a stub that opens the Store unless Store Python is installed: tried last
                if (d.Length > 0 && d.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) < 0) candidates.Add(Path.Combine(d, "python.exe"));
            }
            foreach (string root in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     })
            {
                try
                {
                    candidates.AddRange(Directory.GetDirectories(root, "Python3*").OrderByDescending(VersionOf).Select(d => Path.Combine(d, "python.exe")));
                }
                catch (Exception) { /* no such folder */ }
            }
            foreach (string exe in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                if (File.Exists(exe) && Ask(exe, "-I -c \"import sys; print(sys.version_info[0])\"") == "3") return exe;
            string viaLauncher = Ask("py.exe", "-3 -c \"import sys; print(sys.executable)\"");
            if (!string.IsNullOrEmpty(viaLauncher) && File.Exists(viaLauncher)) return viaLauncher;
            string store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "python.exe");
            return File.Exists(store) && Ask(store, "-I -c \"import sys; print(sys.version_info[0])\"") == "3" ? store : null;
        }

        /// <summary>"Python312" -> 312, so 3.12 sorts above 3.9.</summary>
        static int VersionOf(string dir)
        {
            string digits = new string(Path.GetFileName(dir).Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int v) ? v : 0;
        }

        /// <summary>First line of a short command's output, or null (5 s at most: this runs on AutoCAD's main thread).</summary>
        static string Ask(string exe, string args)
        {
            try
            {
                using (Process p = Process.Start(new ProcessStartInfo(exe, args)
                       {
                           UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                       }))
                {
                    if (p == null) return null;
                    string line = p.StandardOutput.ReadLine();
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        return null;
                    }
                    return p.ExitCode == 0 ? line?.Trim() : null;
                }
            }
            catch (Exception) { return null; }
        }
    }
}
