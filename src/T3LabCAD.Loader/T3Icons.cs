using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace T3LabCAD.Loader
{
    /// <summary>
    /// The ribbon icons of T3Lab Revit MCP and T3Lab CAD MCP (same file in both repos, other namespace), drawn in code in
    /// T3 Space Line (t3lab-revit-api docs/ui-governance/09-ribbon-icon-standard.md): 2-unit round strokes on a 32 grid, no
    /// fill, ink for the main shape, ink-2 for detail, exactly one signal (orange) detail; action badges sit bottom right and
    /// the main shape is cut open there. Dark theme = the same strokes recoloured. One server for the whole set: the MCP
    /// Control icon of t3lab-revit-api (Connect AI); Agent Cursors is that repo's icon too.
    /// </summary>
    internal static class T3Icons
    {
        internal enum Token { Ink, Ink2, Signal }

        const string RackTop = "M6 4h12a2 2 0 0 1 2 2v4a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2v-4a2 2 0 0 1 2 -2z";
        const string RackBottom = "M6 18h12a2 2 0 0 1 2 2v4a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2v-4a2 2 0 0 1 2 -2z";
        const string RackBottomCut = "M16 18h-10a2 2 0 0 0 -2 2v4a2 2 0 0 0 2 2h10";   // open where the badge sits
        const string Leds = "M8 8h0.01M8 22h0.01";                                      // dots: round caps on a zero-length line

        static readonly Dictionary<string, KeyValuePair<Token, string>[]> Set = new Dictionary<string, KeyValuePair<Token, string>[]>
        {
            // Start / Stop: the server + a power badge
            ["power"] = Icon(Token.Ink, RackTop, Token.Ink, RackBottomCut, Token.Ink2, Leds, Token.Signal, "M21 21a4 4 0 1 0 6 0M24 19v5"),
            // Agent Cursors: an agent's pointer + a second one on the element it edits
            ["cursors"] = Icon(Token.Ink2, "M17 19h8a2 2 0 0 1 2 2v4a2 2 0 0 1 -2 2h-8a2 2 0 0 1 -2 -2v-4a2 2 0 0 1 2 -2z",
                Token.Ink, "M6 4v16l4 -4l2 4l2 -1l-2 -4h5z", Token.Signal, "M20 6v7l2 -2h3z"),
            // Connect AI: the server + the cable out to the AI apps
            ["connect"] = Icon(Token.Ink, RackTop, Token.Ink, RackBottom, Token.Ink2, Leds, Token.Signal, "M20 8h4v14h-4M24 15h4"),
            // Status: the server + a heartbeat badge
            ["status"] = Icon(Token.Ink, RackTop, Token.Ink, RackBottomCut, Token.Ink2, Leds, Token.Signal, "M20 24h1l2 -4l3 6l2 -2"),
            // Reload: the server + a refresh badge
            ["reload"] = Icon(Token.Ink, RackTop, Token.Ink, RackBottomCut, Token.Ink2, Leds, Token.Signal, "M28 24a4 4 0 1 1 -4 -4M22 18l2 2l-2 2"),
        };

        static KeyValuePair<Token, string>[] Icon(params object[] parts)
        {
            var list = new List<KeyValuePair<Token, string>>();
            for (int i = 0; i + 1 < parts.Length; i += 2) list.Add(new KeyValuePair<Token, string>((Token)parts[i], (string)parts[i + 1]));
            return list.ToArray();
        }

        public static IEnumerable<string> Names => Set.Keys;

        /// <summary>Stroke colours of the two ribbon themes (dev/icons/tokens.json of t3lab-revit-api).</summary>
        public static Color ColorOf(Token token, bool dark)
        {
            switch (token)
            {
                case Token.Ink: return dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x00, 0x00, 0x00);
                case Token.Ink2: return dark ? Color.FromRgb(0xA3, 0xA3, 0xA3) : Color.FromRgb(0x66, 0x66, 0x66);
                default: return dark ? Color.FromRgb(0xFF, 0x8A, 0x3D) : Color.FromRgb(0xEA, 0x68, 0x0C);
            }
        }

        /// <summary>Icon of size x size pixels at 96 DPI (32 large, 16 small).</summary>
        /// <remarks>Exactly the ribbon slot: AutoCAD shows a bitmap at its pixel size and ignores its DPI (a 64 px image at 192 DPI
        /// showed only its top-left quarter), and the host scales it on high-DPI screens like its own icons. At 16 px a 2-unit
        /// stroke is 1 px centred on a pixel edge; half a pixel up-left lands it on whole pixels.</remarks>
        public static ImageSource Get(string name, int size, bool dark)
        {
            try
            {
                if (!Set.TryGetValue(name, out KeyValuePair<Token, string>[] parts)) return null;
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    double nudge = size < 32 ? -0.5 : 0.0;
                    var transform = new TransformGroup();
                    transform.Children.Add(new ScaleTransform(size / 32.0, size / 32.0));
                    transform.Children.Add(new TranslateTransform(nudge, nudge));
                    dc.PushTransform(transform);
                    foreach (KeyValuePair<Token, string> part in parts)
                    {
                        var pen = new Pen(new SolidColorBrush(ColorOf(part.Key, dark)), 2.0)
                        {
                            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
                        };
                        pen.Freeze();
                        dc.DrawGeometry(null, pen, Geometry.Parse(part.Value));
                    }
                    dc.Pop();
                }
                var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(visual);
                bmp.Freeze();
                return bmp;
            }
            catch (System.Exception) { return null; }   // a button without an icon is better than no ribbon
        }
    }
}
