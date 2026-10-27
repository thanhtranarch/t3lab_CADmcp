using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace T3LabCAD.Loader
{
    /// <summary>"T3Lab" ribbon tab with an "MCP Server" panel (Start / Stop / Status / Reload).</summary>
    internal static class T3Ribbon
    {
        const string TabId = "T3LAB_CAD_MCP_TAB";
        static bool _hooked;

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

        static void Build()
        {
            if (!_hooked)
            {
                _hooked = true;
                // Switching workspace rebuilds the ribbon and drops custom tabs.
                AcApp.SystemVariableChanged += (s, e) =>
                {
                    if (string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase)) Build();
                };
            }

            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null || ribbon.FindTab(TabId) != null) return;

            var source = new RibbonPanelSource { Title = "MCP Server" };
            source.Items.Add(Button("Start", "T3MCPSTART", "Start the MCP server so Claude can connect", Color.FromRgb(0x2E, 0x7D, 0x32), "▶"));
            source.Items.Add(Button("Stop", "T3MCPSTOP", "Stop the MCP server", Color.FromRgb(0xC6, 0x28, 0x28), "■"));
            source.Items.Add(Button("Status", "T3MCPSTATUS", "Show port, tool count and last call", Color.FromRgb(0x15, 0x65, 0xC0), "i"));
            source.Items.Add(Button("Reload", "T3MCPRELOAD", "Reload T3LabCAD.Core.dll after installing a new build", Color.FromRgb(0x5E, 0x35, 0xB1), "↻"));

            var tab = new RibbonTab { Title = "T3Lab", Id = TabId };
            tab.Panels.Add(new RibbonPanel { Source = source });
            ribbon.Tabs.Add(tab);
        }

        static RibbonButton Button(string text, string command, string tip, Color color, string glyph)
        {
            return new RibbonButton
            {
                Text = text,
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                LargeImage = Icon(glyph, color, 32),
                Image = Icon(glyph, color, 16),
                ToolTip = tip,
                CommandParameter = command,
                CommandHandler = new SendCommand(),
            };
        }

        static ImageSource Icon(string glyph, Color color, int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(1, 1, size - 2, size - 2), size * 0.22, size * 0.22);
                var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Symbol"), size * 0.55, Brushes.White, 1.0);
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
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
