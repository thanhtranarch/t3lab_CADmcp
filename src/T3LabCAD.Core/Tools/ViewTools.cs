using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    internal static class ViewTools
    {
        public static void Register()
        {
            Registry.Add("zoom", "Zoom", ToolKind.Write,
                "Zoom the current view: extents (everything), window {p1, p2}, handles (fit these entities), or center {center, height}.",
                ObjR("mode",
                    P("mode", Enum("What to zoom to", "extents", "window", "handles", "center")),
                    P("p1", Pt("window: first corner")),
                    P("p2", Pt("window: second corner")),
                    Handles,
                    P("center", Pt("center: view center")),
                    P("height", Num("center: view height in drawing units")),
                    P("margin", Num("Extra space around the target, as a fraction (default 0.05)"))),
                Zoom);

            Registry.Add("screenshot", "Screenshot", ToolKind.Read,
                "PNG image of the current AutoCAD view of the active drawing, to check what was drawn. Zoom first to frame the area.",
                Obj(P("width", Int("Image width in pixels (default 1280, max 2400)")),
                    P("height", Int("Image height in pixels (default 800, max 1600)")),
                    P("save_path", Str("Also save the PNG to this path"))),
                Screenshot, tx: false);
        }

        static object Zoom(ToolContext c, Args a)
        {
            var db = c.Db;
            double margin = 1 + Math.Max(0, a.Num("margin", 0.05));
            string mode = (a.Str("mode") ?? "extents").ToLowerInvariant();
            Point3d min, max;
            switch (mode)
            {
                case "extents":
                    db.UpdateExt(true);
                    bool model = db.TileMode || db.CurrentSpaceId == SymbolUtilityServices.GetBlockModelSpaceId(db);
                    min = model ? db.Extmin : db.Pextmin;
                    max = model ? db.Extmax : db.Pextmax;
                    if (min.X > max.X) throw new ToolError("The current space is empty.");
                    break;
                case "window":
                    Point3d p1 = a.ReqPt("p1"), p2 = a.ReqPt("p2");
                    min = new Point3d(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), 0);
                    max = new Point3d(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), 0);
                    break;
                case "handles":
                    var ext = new Extents3d();
                    bool any = false;
                    foreach (ObjectId id in Acad.Ids(db, a.Strs("handles")))
                    {
                        Extents3d? b = ((Entity)c.Tr.GetObject(id, OpenMode.ForRead)).Bounds;
                        if (!b.HasValue) continue;
                        if (any) ext.AddExtents(b.Value); else ext = b.Value;
                        any = true;
                    }
                    if (!any) throw new ToolError("None of these entities has a visible extent.");
                    min = ext.MinPoint;
                    max = ext.MaxPoint;
                    break;
                case "center":
                    Point3d center = a.ReqPt("center");
                    double h = a.ReqNum("height") / 2;
                    min = new Point3d(center.X - h, center.Y - h, center.Z);
                    max = new Point3d(center.X + h, center.Y + h, center.Z);
                    break;
                default:
                    throw new ToolError("mode must be extents, window, handles or center.");
            }
            ZoomTo(c.Ed, min, max, margin);
            return new Dictionary<string, object> { ["zoomed"] = mode, ["min"] = Geo.P(min), ["max"] = Geo.P(max) };
        }

        static void ZoomTo(Editor ed, Point3d min, Point3d max, double margin)
        {
            using (ViewTableRecord view = ed.GetCurrentView())
            {
                Matrix3d wcsToDcs =
                    (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                     Matrix3d.Displacement(view.Target - Point3d.Origin) *
                     Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
                var ext = new Extents3d(min, max);
                ext.TransformBy(wcsToDcs);
                double w = Math.Max(ext.MaxPoint.X - ext.MinPoint.X, 1e-6);
                double h = Math.Max(ext.MaxPoint.Y - ext.MinPoint.Y, 1e-6);
                double ratio = view.Width / view.Height;
                view.CenterPoint = new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2);
                if (w / h > ratio) { view.Width = w * margin; view.Height = w * margin / ratio; }
                else { view.Height = h * margin; view.Width = h * margin * ratio; }
                ed.SetCurrentView(view);
            }
        }

        static object Screenshot(ToolContext c, Args a)
        {
            int width = Math.Max(200, Math.Min(2400, a.Int("width", 1280)));
            int height = Math.Max(150, Math.Min(1600, a.Int("height", 800)));
            System.Drawing.Bitmap bmp;
            string source = "view";
            try { bmp = c.Doc.CapturePreviewImage((uint)width, (uint)height); }
            catch (Exception)
            {
                bmp = null;
            }
            if (bmp == null)
            {
                bmp = CaptureMainWindow();
                source = "window";
            }

            using (bmp)
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                string savePath = a.Str("save_path");
                if (!string.IsNullOrEmpty(savePath)) File.WriteAllBytes(savePath, ms.ToArray());
                return new ImageResult
                {
                    Base64 = Convert.ToBase64String(ms.ToArray()),
                    Info = new Dictionary<string, object>
                    {
                        ["document"] = Path.GetFileName(c.Doc.Name),
                        ["width"] = bmp.Width,
                        ["height"] = bmp.Height,
                        ["source"] = source,
                        ["saved_to"] = savePath,
                    },
                };
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        /// <summary>Fallback: what is on screen over the AutoCAD window.</summary>
        static System.Drawing.Bitmap CaptureMainWindow()
        {
            IntPtr hwnd = AcApp.MainWindow.Handle;
            if (!GetWindowRect(hwnd, out Rect r) || r.Right <= r.Left) throw new ToolError("Could not capture the AutoCAD window.");
            var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
            return bmp;
        }
    }
}
