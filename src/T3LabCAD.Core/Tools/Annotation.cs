using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;
using static T3LabCAD.Core.Geo;

namespace T3LabCAD.Core
{
    /// <summary>add_entities types for annotation: table, mleader and the dimension types beyond aligned / linear.</summary>
    internal static class Annotation
    {
        public static readonly string[] Types =
            { "table", "mleader", "dim_radius", "dim_diameter", "dim_angular", "dim_arc", "dim_ordinate", "dim_chain" };

        /// <summary>One entity for every type except dim_chain (see Chain).</summary>
        public static Entity Create(ToolContext c, BlockTableRecord space, string type, Args it)
        {
            switch (type)
            {
                case "table": return Table(c, space, it);
                case "mleader": return MLeaderOf(c, space, it);
                case "dim_radius": return Radius(c, space, it);
                case "dim_diameter": return Diameter(c, space, it);
                case "dim_angular": return Angular(c, space, it);
                case "dim_arc": return ArcLength(c, space, it);
                case "dim_ordinate": return Ordinate(c, space, it);
                default: return null;
            }
        }

        static ObjectId DimStyle(ToolContext c, Args it) => Acad.DimStyle(c.Db, c.Tr, it.Str("style"));

        static string Text(Args it) => it.Str("text") ?? "";

        static Entity Add(ToolContext c, BlockTableRecord space, Entity e, Args it) => EditTools.AddToSpace(c, space, e, it);

        // ---------------------------------------------------------------- table

        static ObjectId TableStyle(ToolContext c, string name)
        {
            if (string.IsNullOrEmpty(name)) return c.Db.Tablestyle;
            var dict = (DBDictionary)c.Tr.GetObject(c.Db.TableStyleDictionaryId, OpenMode.ForRead);
            if (!dict.Contains(name)) throw new ToolError($"Table style '{name}' does not exist (list_named_objects kind=table_style).");
            return dict.GetAt(name);
        }

        static Entity Table(ToolContext c, BlockTableRecord space, Args it)
        {
            IList raw = it.List("rows") ?? throw new ToolError("table needs rows: [[cell, cell...], ...].");
            var data = new List<List<string>>();
            foreach (object row in raw)
            {
                var cells = new List<string>();
                if (row is IList list && !(row is string))
                    foreach (object v in list) cells.Add(v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture));
                else cells.Add(Convert.ToString(row, CultureInfo.InvariantCulture));
                data.Add(cells);
            }
            int cols = data.Count == 0 ? 0 : data.Max(r => r.Count);
            if (cols == 0) throw new ToolError("table rows are empty.");
            string title = it.Str("title");
            bool header = it.Bool("header", true);
            int first = title != null ? 1 : 0;
            int rows = data.Count + first;

            var t = new Table();
            t.SetDatabaseDefaults(c.Db);
            t.TableStyle = TableStyle(c, it.Str("style"));
            t.Position = it.ReqPt("position");
            t.SetSize(rows, cols);
            // the style may merge the first row as a title: start from plain cells
            try { t.UnmergeCells(CellRange.Create(t, 0, 0, rows - 1, cols - 1)); }
            catch (AcRx.Exception) { /* nothing was merged */ }

            if (title != null)
            {
                t.Rows[0].Style = "_TITLE";
                if (cols > 1) t.MergeCells(CellRange.Create(t, 0, 0, 0, cols - 1));
                t.Cells[0, 0].TextString = title;
            }
            for (int r = 0; r < data.Count; r++)
            {
                t.Rows[first + r].Style = header && r == 0 ? "_HEADER" : "_DATA";
                for (int col = 0; col < cols; col++) t.Cells[first + r, col].TextString = col < data[r].Count ? data[r][col] : "";
            }

            if (it.Has("text_height"))
            {
                double h = it.ReqNum("text_height");
                if (h <= 0) throw new ToolError("text_height must be greater than 0.");
                for (int r = 0; r < rows; r++)
                    for (int col = 0; col < cols; col++) t.Cells[r, col].TextHeight = h;
            }
            if (it.Has("col_widths"))
            {
                IList widths = it.List("col_widths");
                for (int col = 0; col < cols; col++)
                {
                    double w = Args.ToDouble(widths[Math.Min(col, widths.Count - 1)], "col_widths");
                    if (w <= 0) throw new ToolError("col_widths must be greater than 0.");
                    t.Columns[col].Width = w;
                }
            }
            if (it.Has("row_height"))
            {
                double h = it.ReqNum("row_height");
                if (h <= 0) throw new ToolError("row_height must be greater than 0.");
                for (int r = 0; r < rows; r++) t.Rows[r].Height = h;
            }
            t.GenerateLayout();
            return Add(c, space, t, it);
        }

        // ---------------------------------------------------------------- multileader

        static Entity MLeaderOf(ToolContext c, BlockTableRecord space, Args it)
        {
            List<Point3d> pts = it.Pts("points");
            if (pts.Count < 2) throw new ToolError("mleader needs points: the arrow point first, the landing point last (2 or more).");
            var ml = new MLeader();
            ml.SetDatabaseDefaults(c.Db);
            if (it.Has("style"))
            {
                string name = it.ReqStr("style");
                var dict = (DBDictionary)c.Tr.GetObject(c.Db.MLeaderStyleDictionaryId, OpenMode.ForRead);
                if (!dict.Contains(name)) throw new ToolError($"Multileader style '{name}' does not exist (list_named_objects kind=mleader_style).");
                ml.MLeaderStyle = dict.GetAt(name);
            }
            ml.ContentType = ContentType.MTextContent;
            var mt = new MText();
            mt.SetDatabaseDefaults(c.Db);
            mt.Contents = EditTools.MTextContents(it.ReqStr("text"));
            if (it.Has("text_height"))
            {
                double h = it.ReqNum("text_height");
                if (h <= 0) throw new ToolError("text_height must be greater than 0.");
                mt.TextHeight = h;
            }
            mt.Location = pts[pts.Count - 1];
            ml.MText = mt;
            // a leader line starts at the landing side; earlier points are put in front, ending with the arrow
            int line = ml.AddLeaderLine(pts[pts.Count - 1]);
            for (int i = pts.Count - 2; i >= 0; i--) ml.AddFirstVertex(line, pts[i]);
            return Add(c, space, ml, it);
        }

        // ---------------------------------------------------------------- dimensions

        /// <summary>center + radius from handle (circle / arc), or center + chord_point.</summary>
        static void CircleOf(ToolContext c, Args it, out Point3d center, out double radius, out Vector3d dir)
        {
            double angle = Rad(it.Num("angle", 45));
            dir = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0);
            if (it.Has("handle"))
            {
                switch (c.Tr.GetObject(Acad.Id(c.Db, it.ReqStr("handle")), OpenMode.ForRead))
                {
                    case Circle ci: center = ci.Center; radius = ci.Radius; return;
                    case Arc ar: center = ar.Center; radius = ar.Radius; return;
                    default: throw new ToolError("handle must be a circle or an arc.");
                }
            }
            center = it.ReqPt("center");
            Point3d chord = it.ReqPt("chord_point");
            radius = center.DistanceTo(chord);
            if (radius < 1e-9) throw new ToolError("chord_point must be away from center.");
            dir = (chord - center).GetNormal();
        }

        static Entity Radius(ToolContext c, BlockTableRecord space, Args it)
        {
            CircleOf(c, it, out Point3d center, out double r, out Vector3d dir);
            return Add(c, space, new RadialDimension(center, center + dir * r, it.Num("leader_length", 0), Text(it), DimStyle(c, it)), it);
        }

        static Entity Diameter(ToolContext c, BlockTableRecord space, Args it)
        {
            CircleOf(c, it, out Point3d center, out double r, out Vector3d dir);
            return Add(c, space, new DiametricDimension(center + dir * r, center - dir * r, it.Num("leader_length", 0), Text(it), DimStyle(c, it)), it);
        }

        static Entity Angular(ToolContext c, BlockTableRecord space, Args it)
        {
            Point3d center = it.ReqPt("center"), p1 = it.ReqPt("p1"), p2 = it.ReqPt("p2");
            Vector3d v1 = p1 - center, v2 = p2 - center;
            if (v1.Length < 1e-9 || v2.Length < 1e-9) throw new ToolError("p1 and p2 must be away from center.");
            Point3d arcPoint;
            if (it.Has("arc_point")) arcPoint = it.ReqPt("arc_point");
            else
            {
                Vector3d bisector = v1.GetNormal() + v2.GetNormal();
                if (bisector.Length < 1e-9) bisector = v1.GetPerpendicularVector();
                arcPoint = center + bisector.GetNormal() * Math.Max(v1.Length, v2.Length) * 0.75;
            }
            return Add(c, space, new Point3AngularDimension(center, p1, p2, arcPoint, Text(it), DimStyle(c, it)), it);
        }

        static Entity ArcLength(ToolContext c, BlockTableRecord space, Args it)
        {
            Point3d center, p1, p2, arcPoint;
            if (it.Has("handle"))
            {
                if (!(c.Tr.GetObject(Acad.Id(c.Db, it.ReqStr("handle")), OpenMode.ForRead) is Arc arc)) throw new ToolError("handle must be an arc.");
                center = arc.Center;
                p1 = arc.StartPoint;
                p2 = arc.EndPoint;
                Point3d mid = arc.GetPointAtDist(arc.Length / 2);
                arcPoint = it.Has("arc_point") ? it.ReqPt("arc_point") : center + (mid - center) * 1.15;
            }
            else
            {
                center = it.ReqPt("center");
                p1 = it.ReqPt("p1");
                p2 = it.ReqPt("p2");
                arcPoint = it.ReqPt("arc_point");
            }
            return Add(c, space, new ArcDimension(center, p1, p2, arcPoint, Text(it), DimStyle(c, it)), it);
        }

        static Entity Ordinate(ToolContext c, BlockTableRecord space, Args it)
        {
            Point3d feature = it.ReqPt("point"), leaderEnd = it.ReqPt("leader_end");
            string axis = it.Str("axis")?.Trim().ToLowerInvariant();
            // X datum when the leader runs mostly vertically, as ORDINATE does
            bool useX = axis == null ? Math.Abs(leaderEnd.Y - feature.Y) >= Math.Abs(leaderEnd.X - feature.X) : axis == "x";
            if (axis != null && axis != "x" && axis != "y") throw new ToolError("axis must be x or y.");
            var dim = new OrdinateDimension(useX, feature, leaderEnd, Text(it), DimStyle(c, it));
            if (it.Has("origin")) dim.Origin = it.ReqPt("origin");
            return Add(c, space, dim, it);
        }

        /// <summary>dim_chain: continued (end to end) or baseline (all from the first point) linear dimensions.</summary>
        public static List<Entity> Chain(ToolContext c, BlockTableRecord space, Args it)
        {
            List<Point3d> pts = it.Pts("points");
            if (pts.Count < 2) throw new ToolError("dim_chain needs 2 or more points.");
            Point3d dimPoint = it.ReqPt("dim_point");
            double rotation = Rad(it.Num("rotation", 0));
            string mode = (it.Str("mode") ?? "continue").Trim().ToLowerInvariant();
            if (mode != "continue" && mode != "baseline") throw new ToolError("mode must be continue or baseline.");
            ObjectId style = DimStyle(c, it);

            var normal = new Vector3d(-Math.Sin(rotation), Math.Cos(rotation), 0);
            double side = Math.Sign((dimPoint - pts[0]).DotProduct(normal));
            if (side == 0) side = 1;
            double spacing = it.Has("spacing") ? it.ReqNum("spacing") : BaselineSpacing(c.Db);

            var made = new List<Entity>();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                RotatedDimension dim = mode == "continue"
                    ? new RotatedDimension(rotation, pts[i], pts[i + 1], dimPoint, "", style)
                    : new RotatedDimension(rotation, pts[0], pts[i + 1], dimPoint + normal * side * spacing * i, "", style);
                made.Add(Add(c, space, dim, it));
            }
            return made;
        }

        /// <summary>DIMDLI x DIMSCALE (the BASELINE spacing), with a fallback when it is 0.</summary>
        static double BaselineSpacing(Database db)
        {
            double scale = db.Dimscale > 0 ? db.Dimscale : 1;
            double dli = db.Dimdli > 0 ? db.Dimdli : 3.75;
            return dli * scale;
        }
    }
}
