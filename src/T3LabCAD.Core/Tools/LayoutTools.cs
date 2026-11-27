using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>Paper space layouts and their viewports. Rename / delete: rename_object / delete_object kind=layout.</summary>
    internal static class LayoutTools
    {
        public static void Register()
        {
            Registry.Add("create_layout", "Create layout", ToolKind.Write,
                "Create a paper space layout (sheet), empty or as a copy of another layout (page setup, title block, viewports). " +
                "Rename / delete layouts with rename_object / delete_object kind=layout.",
                ObjR("name",
                    P("name", Str("New layout name")),
                    P("copy_from", Str("Copy this layout (default: new empty layout)")),
                    P("activate", Bool("Make it the current layout (default false)"))),
                CreateLayout, tx: false, scope: ToolScope.Active);

            Registry.Add("add_viewport", "Add viewport", ToolKind.Write,
                "Add a viewport to a paper space layout showing part of model space (the layout becomes current). " +
                "Paper rectangle: corner1 + corner2 in paper units (default: the layout limits, 5% margin). " +
                "Model area: view_window [[x1, y1], [x2, y2]] (fitted) or view_center. scale = paper units per model unit: 0.01 or 1:100.",
                ObjR("layout",
                    P("layout", Str("Paper space layout name")),
                    P("corner1", Pt("Paper: first corner of the viewport")),
                    P("corner2", Pt("Paper: opposite corner")),
                    P("view_window", Pts("Model: [[x1, y1], [x2, y2]] area to show")),
                    P("view_center", Pt("Model: point at the viewport center")),
                    P("scale", Any("Viewport scale, e.g. 1:100, 1/50 or 0.01 (default: fit view_window or the model extents)")),
                    P("layer", Str("Layer for the viewport (created if missing)")),
                    P("locked", Bool("Lock the display (default false)"))),
                AddViewport, tx: false, scope: ToolScope.Active);

            Registry.Add("set_viewport", "Edit viewport", ToolKind.Write,
                "Change a layout viewport (handle from query_entities types=[VIEWPORT] or add_viewport): scale, view_center or view_window " +
                "(fit), lock, on / off, layer, and per-viewport layer freeze (freeze_layers / thaw_layers, wildcards allowed, like VPLAYER). " +
                "locked is the state after the call: a locked viewport can change its view with locked=true (stays locked) or false. " +
                "The viewport layout becomes current.",
                ObjR("handle",
                    P("handle", Str("Viewport handle")),
                    P("scale", Any("Viewport scale, e.g. 1:100, 1/50 or 0.01")),
                    P("view_center", Pt("Model point at the viewport center")),
                    P("view_window", Pts("Model area [[x1, y1], [x2, y2]] to fit (scale from it unless scale is given)")),
                    P("locked", Bool("Lock / unlock the display")),
                    P("on", Bool("Viewport on / off")),
                    P("freeze_layers", Strs("Layers to freeze in this viewport only, wildcards allowed")),
                    P("thaw_layers", Strs("Layers to thaw in this viewport, wildcards allowed")),
                    P("layer", Str("Layer for the viewport (created if missing)"))),
                SetViewport, tx: false, scope: ToolScope.Active);
        }

        static object CreateLayout(ToolContext c, Args a)
        {
            LayoutManager lm = LayoutManager.Current;
            string name = a.ReqStr("name").Trim();
            Acad.ValidateName(name, "layout");
            if (name.Equals("Model", StringComparison.OrdinalIgnoreCase) || lm.LayoutExists(name))
                throw new ToolError($"A layout named '{name}' already exists.");

            string from = a.Str("copy_from");
            if (from != null)
            {
                if (!lm.LayoutExists(from)) throw new ToolError($"Layout '{from}' does not exist (see list_layouts).");
                if (from.Equals("Model", StringComparison.OrdinalIgnoreCase)) throw new ToolError("The Model layout cannot be copied.");
                lm.CopyLayout(from, name);
            }
            else
            {
                lm.CreateLayout(name);
            }
            if (a.Bool("activate", false)) lm.CurrentLayout = name;

            int tabOrder;
            using (var tr = c.Db.TransactionManager.StartTransaction())
            {
                tabOrder = ((Layout)tr.GetObject(lm.GetLayoutId(name), OpenMode.ForRead)).TabOrder;
                tr.Commit();
            }
            return new Dictionary<string, object>
            {
                ["layout"] = name,
                ["copied_from"] = from,
                ["tab_order"] = tabOrder,
                ["current_layout"] = lm.CurrentLayout,
            };
        }

        /// <summary>Paper units per model unit: "1:100" and "1/100" = 0.01; a plain number is the factor itself (2 = 2:1).</summary>
        internal static double ParseScale(object raw)
        {
            string s = Convert.ToString(raw, CultureInfo.InvariantCulture).Trim().Replace(" ", "");
            int sep = s.IndexOfAny(new[] { ':', '/' });
            double value;
            if (sep > 0)
            {
                double paper = Args.ToDouble(s.Substring(0, sep), "scale"), model = Args.ToDouble(s.Substring(sep + 1), "scale");
                if (model <= 0) throw new ToolError("scale: the model part must be greater than 0.");
                value = paper / model;
            }
            else
            {
                value = Args.ToDouble(s, "scale");
            }
            if (value <= 0 || double.IsInfinity(value)) throw new ToolError("scale must be greater than 0, e.g. 1:100 or 0.01.");
            return value;
        }

        internal static string ScaleText(double scale) =>
            scale >= 1 ? $"{Geo.R(scale)}:1" : $"1:{Geo.R(1 / scale)}";

        static List<ObjectId> LayersMatching(Database db, Transaction tr, List<string> patterns)
        {
            var ids = new List<ObjectId>();
            if (patterns.Count == 0) return ids;
            foreach (ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
            {
                var layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!layer.IsErased && Wild.Any(layer.Name, patterns)) ids.Add(id);
            }
            if (ids.Count == 0) throw new ToolError($"No layer matches {string.Join(", ", patterns)}.");
            return ids;
        }

        static object SetViewport(ToolContext c, Args a)
        {
            var db = c.Db;
            LayoutManager lm = LayoutManager.Current;
            ObjectId id = Acad.Id(db, a.ReqStr("handle"));
            string layoutName;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Viewport vp)) throw new ToolError("handle is not a viewport (query_entities types=[VIEWPORT]).");
                var owner = (BlockTableRecord)tr.GetObject(vp.OwnerId, OpenMode.ForRead);
                if (!owner.IsLayout || owner.ObjectId == SymbolUtilityServices.GetBlockModelSpaceId(db))
                    throw new ToolError("This viewport is not on a paper space layout.");
                ObjectId firstViewport = owner.Cast<ObjectId>().FirstOrDefault(e => e.ObjectClass.DxfName == "VIEWPORT");
                if (firstViewport == id) throw new ToolError("This is the layout's own paper space viewport, not a viewport placed on it.");
                layoutName = ((Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead)).LayoutName;
                tr.Commit();
            }
            if (!lm.CurrentLayout.Equals(layoutName, StringComparison.OrdinalIgnoreCase)) lm.CurrentLayout = layoutName;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var vp = (Viewport)tr.GetObject(id, OpenMode.ForWrite);
                bool changesView = a.Has("scale") || a.Has("view_center") || a.Has("view_window");
                if (vp.Locked && changesView)
                {
                    // locked is the state after the call: true changes the view and keeps it locked
                    if (!a.Has("locked"))
                        throw new ToolError("The viewport is locked: pass locked=true (change it and keep it locked) or locked=false (unlock it).");
                    vp.Locked = false;
                }

                // ViewCenter is in the viewport's display coordinates (target, direction and twist), the API takes WCS points
                Matrix3d dcsToWcs = Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget) *
                                    Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin) *
                                    Matrix3d.PlaneToWorld(vp.ViewDirection);
                Matrix3d wcsToDcs = dcsToWcs.Inverse();
                if (a.Has("view_window"))
                {
                    List<Point3d> w = a.Pts("view_window");
                    if (w.Count != 2) throw new ToolError("view_window must be [[x1, y1], [x2, y2]].");
                    var corners = new[]
                    {
                        new Point3d(w[0].X, w[0].Y, 0), new Point3d(w[1].X, w[0].Y, 0),
                        new Point3d(w[1].X, w[1].Y, 0), new Point3d(w[0].X, w[1].Y, 0),
                    }.Select(p => p.TransformBy(wcsToDcs)).ToList();
                    double minX = corners.Min(p => p.X), maxX = corners.Max(p => p.X);
                    double minY = corners.Min(p => p.Y), maxY = corners.Max(p => p.Y);
                    double ww = maxX - minX, wh = maxY - minY;
                    if (ww < 1e-9 || wh < 1e-9) throw new ToolError("view_window must span a rectangle.");
                    vp.ViewCenter = new Point2d((minX + maxX) / 2, (minY + maxY) / 2);
                    if (!a.Has("scale")) vp.CustomScale = Math.Min(vp.Width / ww, vp.Height / wh);
                }
                if (a.Has("view_center"))
                {
                    Point3d vc = a.ReqPt("view_center").TransformBy(wcsToDcs);
                    vp.ViewCenter = new Point2d(vc.X, vc.Y);
                }
                if (a.Has("scale")) vp.CustomScale = ParseScale(a.Raw("scale"));

                List<ObjectId> freeze = LayersMatching(db, tr, a.Strs("freeze_layers"));
                if (freeze.Count > 0) vp.FreezeLayersInViewport(freeze.GetEnumerator());
                List<ObjectId> thaw = LayersMatching(db, tr, a.Strs("thaw_layers"));
                if (thaw.Count > 0) vp.ThawLayersInViewport(thaw.GetEnumerator());
                if (a.Has("layer")) vp.LayerId = Acad.EnsureLayer(db, tr, a.ReqStr("layer"));
                if (a.Has("on")) vp.On = a.Bool("on", true);
                if (a.Has("locked")) vp.Locked = a.Bool("locked", false);

                var frozen = new List<object>();
                foreach (ObjectId lid in vp.GetFrozenLayers())
                    frozen.Add(((LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead)).Name);
                var result = new Dictionary<string, object>
                {
                    ["handle"] = vp.Handle.ToString(),
                    ["layout"] = layoutName,
                    ["scale"] = Geo.R(vp.CustomScale),
                    ["scale_text"] = vp.CustomScale > 0 ? ScaleText(vp.CustomScale) : "?",
                    ["view_center"] = Geo.P(new Point3d(vp.ViewCenter.X, vp.ViewCenter.Y, 0).TransformBy(dcsToWcs)),
                    ["locked"] = vp.Locked,
                    ["on"] = vp.On,
                    ["frozen_layers"] = frozen,
                };
                tr.Commit();
                c.NeedsRegen = true;
                return result;
            }
        }

        static object AddViewport(ToolContext c, Args a)
        {
            var db = c.Db;
            LayoutManager lm = LayoutManager.Current;
            string name = a.ReqStr("layout");
            if (name.Equals("Model", StringComparison.OrdinalIgnoreCase)) throw new ToolError("Viewports go on a paper space layout, not Model.");
            if (!lm.LayoutExists(name)) throw new ToolError($"Layout '{name}' does not exist (see list_layouts, or create_layout).");
            // Viewport.On only works on the current layout. The first switch to a new layout sets up its paper;
            // with LAYOUTCREATEVIEWPORT=1 AutoCAD would also add its own viewport next to the one asked for.
            if (!lm.CurrentLayout.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                object autoViewport = AcApp.GetSystemVariable("LAYOUTCREATEVIEWPORT");
                try
                {
                    AcApp.SetSystemVariable("LAYOUTCREATEVIEWPORT", (short)0);
                    lm.CurrentLayout = name;
                }
                finally
                {
                    AcApp.SetSystemVariable("LAYOUTCREATEVIEWPORT", autoViewport);
                }
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var layout = (Layout)tr.GetObject(lm.GetLayoutId(name), OpenMode.ForRead);
                Point3d p1, p2;
                if (a.Has("corner1") || a.Has("corner2"))
                {
                    p1 = a.ReqPt("corner1");
                    p2 = a.ReqPt("corner2");
                }
                else
                {
                    Extents2d lim = layout.Limits;
                    double mx = (lim.MaxPoint.X - lim.MinPoint.X) * 0.05, my = (lim.MaxPoint.Y - lim.MinPoint.Y) * 0.05;
                    if (mx <= 0 || my <= 0) throw new ToolError("This layout has no paper size yet: pass corner1 and corner2.");
                    p1 = new Point3d(lim.MinPoint.X + mx, lim.MinPoint.Y + my, 0);
                    p2 = new Point3d(lim.MaxPoint.X - mx, lim.MaxPoint.Y - my, 0);
                }
                double width = Math.Abs(p2.X - p1.X), height = Math.Abs(p2.Y - p1.Y);
                if (width < 1e-6 || height < 1e-6) throw new ToolError("corner1 and corner2 must span a rectangle.");

                Point2d viewCenter;
                double? fit = null;
                if (a.Has("view_window"))
                {
                    List<Point3d> w = a.Pts("view_window");
                    if (w.Count != 2) throw new ToolError("view_window must be [[x1, y1], [x2, y2]].");
                    double ww = Math.Abs(w[1].X - w[0].X), wh = Math.Abs(w[1].Y - w[0].Y);
                    if (ww < 1e-9 || wh < 1e-9) throw new ToolError("view_window must span a rectangle.");
                    viewCenter = new Point2d((w[0].X + w[1].X) / 2, (w[0].Y + w[1].Y) / 2);
                    fit = Math.Min(width / ww, height / wh);
                }
                else if (a.Has("view_center"))
                {
                    Point3d vc = a.ReqPt("view_center");
                    viewCenter = new Point2d(vc.X, vc.Y);
                }
                else
                {
                    Point3d min = db.Extmin, max = db.Extmax;
                    if (min.X > max.X || min.Y > max.Y) { min = Point3d.Origin; max = new Point3d(width, height, 0); }
                    viewCenter = new Point2d((min.X + max.X) / 2, (min.Y + max.Y) / 2);
                    double ew = Math.Max(max.X - min.X, 1e-6), eh = Math.Max(max.Y - min.Y, 1e-6);
                    fit = Math.Min(width / ew, height / eh) * 0.95;
                }
                double scale = a.Has("scale") ? ParseScale(a.Raw("scale")) : fit ?? 1.0;
                ObjectId layerId = a.Has("layer") ? Acad.EnsureLayer(db, tr, a.ReqStr("layer")) : ObjectId.Null;
                var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                string layoutName = layout.LayoutName;

                var vp = new Viewport();
                vp.SetDatabaseDefaults(db);
                vp.CenterPoint = new Point3d((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2, 0);
                vp.Width = width;
                vp.Height = height;
                if (!layerId.IsNull) vp.LayerId = layerId;
                Acad.Append(space, vp, tr);
                vp.ViewDirection = Vector3d.ZAxis;
                vp.ViewTarget = Point3d.Origin;
                vp.TwistAngle = 0;
                vp.ViewCenter = viewCenter;
                vp.CustomScale = scale;
                vp.On = true;
                if (a.Bool("locked", false)) vp.Locked = true;
                string handle = vp.Handle.ToString();
                tr.Commit();
                c.NeedsRegen = true;
                return new Dictionary<string, object>
                {
                    ["handle"] = handle,
                    ["layout"] = layoutName,
                    ["center"] = Geo.P(new Point2d((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2)),
                    ["width"] = Geo.R(width),
                    ["height"] = Geo.R(height),
                    ["view_center"] = Geo.P(viewCenter),
                    ["scale"] = Geo.R(scale),
                    ["scale_text"] = ScaleText(scale),
                };
            }
        }
    }
}
