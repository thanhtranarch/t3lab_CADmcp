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
    /// </summary>
    internal static class T3Ribbon
    {
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
                    catch (Exception ex) { CoreHost.Write("T3LabCAD: ribbon panel not updated: " + ex.Message); }
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
                        CoreHost.Write("T3LabCAD: this workspace has no Add-ins ribbon tab; the T3Lab MCP panel is on a tab named Add-ins of its own.");
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
            source.Items.Add(Button("Start", "T3MCPSTART", "Start the MCP server so Claude can connect", "start"));
            source.Items.Add(Button("Stop", "T3MCPSTOP", "Stop the MCP server", "stop"));
            source.Items.Add(Button("Status", "T3MCPSTATUS", "Show port, tool count, the call queue and the last call", "status"));
            source.Items.Add(Button("Reload", "T3MCPRELOAD", "Reload T3LabCAD.Core.dll after installing a new build", "reload"));
            source.Items.Add(Button("Agent\nCursors", "T3CURSORS", "Show what AI apps do in AutoCAD: a bar with each app, its goal and steps, and pointers on the drawing", "cursors"));
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
                        CoreHost.Write("T3LabCAD: ribbon panel not created: " + ex.Message);
                    }
                };
            }
            _watch.Start();
        }

        static readonly List<KeyValuePair<RibbonButton, string>> Buttons = new List<KeyValuePair<RibbonButton, string>>();

        static RibbonButton Button(string text, string command, string tip, string icon)
        {
            bool dark = IsDarkTheme();
            var button = new RibbonButton
            {
                Text = text,
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                LargeImage = Icon(icon, 32, dark),
                Image = Icon(icon, 16, dark),
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
                item.Key.LargeImage = Icon(item.Value, 32, dark);
                item.Key.Image = Icon(item.Value, 16, dark);
            }
        }

        static bool IsDarkTheme()
        {
            try { return Convert.ToInt32(AcApp.GetSystemVariable("COLORTHEME")) == 0; }
            catch { return true; }   // AutoCAD's default theme is dark
        }

        // Icon family (T3Lab style: round 2-unit strokes on a 32 grid, one orange accent): every button is
        // the MCP server (two rack units) with an orange badge saying what the button does.
        static readonly string[] Server =
        {
            "M6,4 H20 Q22,4 22,6 V10 Q22,12 20,12 H6 Q4,12 4,10 V6 Q4,4 6,4 Z",
            "M6,15 H20 Q22,15 22,17 V21 Q22,23 20,23 H6 Q4,23 4,21 V17 Q4,15 6,15 Z",
        };
        static readonly string[] ServerDetail = { "M12,8 H17", "M12,19 H17" };
        static readonly string[] ServerLeds = { "M8,8 L8,8.01", "M8,19 L8,19.01" };
        static readonly Dictionary<string, string> Badge = new Dictionary<string, string>
        {
            ["start"] = "M22.6,20.8 L27.4,24 L22.6,27.2 Z",
            ["stop"] = "M21.8,21.8 H26.2 V26.2 H21.8 Z",
            ["status"] = "M19.8,24 H21.4 L22.7,21.2 L25.3,26.8 L26.6,24 H28.2",
            ["reload"] = "M27.6,24 A3.6,3.6 0 1 1 26.4,21.3 M27,19.4 V21.6 H24.8",
            ["cursors"] = "M21.6,20.4 L21.6,27.4 L23.4,25.7 L24.8,28.2 L26,27.6 L24.7,25.2 L27.2,25 Z",
        };
        // Play / stop read as media glyphs only when solid; drawn hollow they looked like "D" and "o".
        static readonly HashSet<string> SolidBadge = new HashSet<string> { "start", "stop", "cursors" };

        /// <summary>Icon of `size` x `size` pixels at 96 DPI.</summary>
        /// <remarks>AutoCAD's ribbon shows a bitmap at its pixel size and ignores its DPI: a 64 px image at
        /// 192 DPI showed only its top-left quarter (one rack, no badge). So the bitmap is exactly the
        /// ribbon slot (32 large / 16 small) and AutoCAD scales it on high-DPI screens like any other icon.</remarks>
        static ImageSource Icon(string name, int size, bool dark)
        {
            Color ink = dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1A, 0x1A, 0x1A);
            Color ink2 = dark ? Color.FromRgb(0xA3, 0xA3, 0xA3) : Color.FromRgb(0x66, 0x66, 0x66);
            Color signal = dark ? Color.FromRgb(0xF2, 0x75, 0x22) : Color.FromRgb(0xEA, 0x68, 0x0C);
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // At 16 px a 2-unit stroke is 1 px wide and centred on a pixel edge (blurred over 2 px);
                // moving up-left half a pixel lands it on whole pixels, and the badge still fits inside 16 px.
                double nudge = size < 32 ? -0.5 : 0.0;
                var transform = new TransformGroup();
                transform.Children.Add(new ScaleTransform(size / 32.0, size / 32.0));
                transform.Children.Add(new TranslateTransform(nudge, nudge));
                dc.PushTransform(transform);
                // The server is cut back around the badge so the two never touch.
                dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(0, 0, 32, 32)), new EllipseGeometry(new Point(24, 24), 8.8, 8.8)));
                foreach (string d in Server) Stroke(dc, d, ink, 2.0);
                foreach (string d in ServerDetail) Stroke(dc, d, ink2, 2.0);
                foreach (string d in ServerLeds) Stroke(dc, d, ink2, 3.0);
                dc.Pop();
                var fill = new SolidColorBrush(signal);
                dc.DrawEllipse(fill, new Pen(fill, 2.0), new Point(24, 24), 6.5, 6.5);
                Stroke(dc, Badge[name], Colors.White, 1.8, SolidBadge.Contains(name));
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        static void Stroke(DrawingContext dc, string path, Color color, double width, bool fill = false)
        {
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, width)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(fill ? brush : null, pen, Geometry.Parse(path));
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
