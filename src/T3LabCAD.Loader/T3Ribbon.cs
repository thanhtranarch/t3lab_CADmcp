using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace T3LabCAD.Loader
{
    /// <summary>
    /// Panel "T3Lab MCP" (Start / Stop / Status / Reload / Agent Cursors) on AutoCAD's own Add-ins tab (owner's request,
    /// 2026-11-27: no tab of our own). The tab's id is not documented (CUIx ids may carry a menu-group prefix such as
    /// "ACAD."), so it is found by the end of its id, then by title. A timer keeps looking for WatchS after start or a
    /// workspace switch: CUIx tabs can arrive after the ribbon. Only when none has appeared after RetryS does the panel go
    /// on a stand-in tab titled "Add-ins", and it moves to the real tab if that shows up while the timer still runs.
    /// Same buttons, order, names, icons (T3Icons) and tooltips as T3Lab Revit MCP's panel on Revit's Add-Ins tab:
    /// [Start / Stop] [Agent Cursors] [Connect AI] [Status | Reload].
    /// </summary>
    internal static class T3Ribbon
    {
        const string Host = "AutoCAD";
        const string PanelId = "T3LAB_CAD_MCP_PANEL", FallbackTabId = "T3LAB_CAD_ADDINS_FALLBACK", OldTabId = "T3LAB_CAD_MCP_TAB";
        const double RetryS = 15, WatchS = 120;
        static readonly string[] AddinsIds = { "ID_TabAddins", "ID_TabAddIns", "ID_ADDINSTAB", "ID_TabPlugins" };
        static readonly string[] AddinsIdParts = { "tabaddins", "tabplugins", "addinstab", "pluginstab" };
        static readonly string[] AddinsTitles =
        {
            "Add-ins", "Add-Ins", "Plug-ins", "Zusatzmodule", "Complementos", "Compléments", "Componenti aggiuntivi", "Doplňky",
            "Dodatki", "Kiegészítők", "Надстройки", "附加模块", "增益集", "アドイン", "애드인",
        };
        static bool _hooked, _reported;
        static DateTime _firstTry;
        static System.Windows.Threading.DispatcherTimer _watch;

        internal static void Init()
        {
            if (ComponentManager.Ribbon == null)
            {
                ComponentManager.ItemInitialized += OnItemInitialized;
                return;
            }
            Build();
        }

        static void OnItemInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            Build();
        }

        /// <summary>AutoCAD's Add-ins tab: an id ending in a known one, else its title, else an id that says add-ins.</summary>
        static RibbonTab FindAddins(RibbonControl ribbon)
        {
            var tabs = ribbon.Tabs.Where(t => t != null && t.Id != FallbackTabId && t.Id != OldTabId).ToList();
            return tabs.FirstOrDefault(t => AddinsIds.Any(id => (t.Id ?? "").EndsWith(id, StringComparison.OrdinalIgnoreCase)))
                ?? tabs.FirstOrDefault(t => AddinsTitles.Any(n => string.Equals((t.Title ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase)))
                ?? tabs.FirstOrDefault(t => AddinsIdParts.Any(part => (t.Id ?? "").IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        static bool HasPanel(RibbonTab tab) => tab.Panels.Any(p => p?.Source?.Id == PanelId);

        static void Build()
        {
            if (!_hooked)
            {
                _hooked = true;
                AcApp.SystemVariableChanged += (s, e) =>
                {
                    try
                    {
                        // Switching workspace rebuilds the ribbon and drops panels added at run time.
                        if (string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase)) { _firstTry = default; Build(); }
                        else if (string.Equals(e.Name, "COLORTHEME", StringComparison.OrdinalIgnoreCase)) Repaint();
                    }
                    catch (Exception ex) { CoreHost.Write("T3Lab CAD MCP: ribbon panel not updated: " + ex.Message); }
                };
            }

            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;
            DateTime now = DateTime.Now;
            if (_firstTry == default)
            {
                _firstTry = now;
                Watch();
            }

            RemoveTab(ribbon, OldTabId);   // the "T3Lab" tab of 1.4 and earlier (a Core reload never left one, a Loader update can)
            RibbonTab tab = FindAddins(ribbon);
            if (tab == null)
            {
                if ((now - _firstTry).TotalSeconds < RetryS) return;   // the timer looks again
                tab = ribbon.FindTab(FallbackTabId);
                if (tab == null)
                {
                    tab = new RibbonTab { Title = "Add-ins", Id = FallbackTabId };
                    ribbon.Tabs.Add(tab);
                    if (!_reported)
                    {
                        _reported = true;
                        CoreHost.Write("T3Lab CAD MCP: this workspace has no Add-ins ribbon tab; the T3Lab MCP panel is on a tab named Add-ins of its own.");
                    }
                }
            }
            else if (ribbon.FindTab(FallbackTabId) != null)
            {
                RemoveTab(ribbon, FallbackTabId);   // the real tab is here now
            }
            if (HasPanel(tab)) return;

            Buttons.Clear();
            var source = new RibbonPanelSource { Title = "T3Lab MCP", Id = PanelId };
            source.Items.Add(Button("Start /\nStop", "T3MCP", "power", true,
                $"Start or stop the MCP server that lets AI apps (Claude, ChatGPT / Codex, Cursor, Gemini...) work in this {Host}. " +
                $"Stopped, it stays stopped at the next {Host} start until you press Start again."));
            source.Items.Add(Button("Agent\nCursors", "T3CURSORS", "cursors", true,
                $"Show what each AI app does in {Host}: its goal, the current step, the last calls and the entities it touched. " +
                "Pause AI changes at any time. Press again to turn it off."));
            source.Items.Add(Button("Connect\nAI", "T3MCPCONNECT", "connect", true,
                $"Add this {Host} to Claude Desktop, Claude Code, Codex, Cursor, VS Code, Gemini CLI, Windsurf and Antigravity in one click, " +
                "or check what is connected."));
            // Status and Reload stacked, as on Revit's panel
            var row = new RibbonRowPanel();
            row.Items.Add(Button("Status", "T3MCPSTATUS", "status", false,
                "Server status: port, tools, calls, the call queue, Agent Cursors and the log folder."));
            row.Items.Add(new RibbonRowBreak());
            row.Items.Add(Button("Reload", "T3MCPRELOAD", "reload", false,
                $"Load T3LabCAD.Core.dll again after an update, without restarting {Host}."));
            source.Items.Add(row);
            tab.Panels.Add(new RibbonPanel { Source = source });
        }

        static void RemoveTab(RibbonControl ribbon, string id)
        {
            try
            {
                RibbonTab old = ribbon.FindTab(id);
                if (old != null) ribbon.Tabs.Remove(old);
            }
            catch (Exception) { /* the ribbon is being rebuilt */ }
        }

        /// <summary>
        /// Look again every second until WatchS has passed (a timer on the main thread's dispatcher: AutoCAD's Idle event
        /// may not come again while nobody touches AutoCAD). Stops early once the panel is on the real Add-ins tab.
        /// </summary>
        static void Watch()
        {
            if (_watch == null)
            {
                _watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _watch.Tick += (s, e) =>
                {
                    try
                    {
                        RibbonControl ribbon = ComponentManager.Ribbon;
                        RibbonTab real = ribbon == null ? null : FindAddins(ribbon);
                        bool settled = real != null && HasPanel(real) && ribbon.FindTab(FallbackTabId) == null;
                        if (settled || (DateTime.Now - _firstTry).TotalSeconds > WatchS) _watch.Stop();
                        else Build();
                    }
                    catch (Exception ex)
                    {
                        _watch.Stop();
                        CoreHost.Write("T3Lab CAD MCP: ribbon panel not created: " + ex.Message);
                    }
                };
            }
            _watch.Start();
        }

        static readonly List<KeyValuePair<RibbonButton, string>> Buttons = new List<KeyValuePair<RibbonButton, string>>();

        static RibbonButton Button(string text, string command, string icon, bool large, string tip)
        {
            bool dark = IsDarkTheme();
            var button = new RibbonButton
            {
                Text = text,
                ShowText = true,
                ShowImage = true,
                Size = large ? RibbonItemSize.Large : RibbonItemSize.Standard,
                Orientation = large ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal,
                LargeImage = T3Icons.Get(icon, 32, dark),
                Image = T3Icons.Get(icon, 16, dark),
                ToolTip = tip,
                CommandParameter = command,
                CommandHandler = new SendCommand(),
            };
            Buttons.Add(new KeyValuePair<RibbonButton, string>(button, icon));
            return button;
        }

        static void Repaint()
        {
            bool dark = IsDarkTheme();
            foreach (var item in Buttons)
            {
                item.Key.LargeImage = T3Icons.Get(item.Value, 32, dark);
                item.Key.Image = T3Icons.Get(item.Value, 16, dark);
            }
        }

        static bool IsDarkTheme()
        {
            try { return Convert.ToInt32(AcApp.GetSystemVariable("COLORTHEME")) == 0; }
            catch { return true; }   // AutoCAD's default theme is dark
        }

        /// <summary>Ribbon buttons run their command through the command line (cancelling whatever is active).</summary>
        sealed class SendCommand : ICommand
        {
            public event EventHandler CanExecuteChanged { add { } remove { } }

            public bool CanExecute(object parameter) => true;

            public void Execute(object parameter)
            {
                string command = (parameter as RibbonButton)?.CommandParameter as string ?? parameter as string;
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null || string.IsNullOrEmpty(command)) return;
                doc.SendStringToExecute("\x03\x03_" + command + " ", true, false, true);
            }
        }
    }
}
