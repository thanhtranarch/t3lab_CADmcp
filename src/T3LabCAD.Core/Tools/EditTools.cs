using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;
using static T3LabCAD.Core.Geo;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    internal static class EditTools
    {
        static readonly string[] CommonKeys = { "layer", "color", "linetype", "lineweight" };

        public static void Register()
        {
            Registry.Add("add_entities", "Draw entities", ToolKind.Write,
                "Create entities in one undo step (all or nothing). Each item has a type and its fields; all accept layer (created if missing), " +
                "color, linetype, lineweight. Types: " +
                "line {start, end}; polyline {points, closed, bulges, width}; rectangle {corner1, corner2}; circle {center, radius}; " +
                "arc {center, radius, start_angle, end_angle} or {points: [p1, p2, p3]}; ellipse {center, major_axis [dx, dy], ratio}; point {position}; " +
                "text {position, text, height, rotation, align (left, center, right, middle_center, top_left...), style}; " +
                "mtext {position, text (\\\\n for new line), height, width, rotation, attachment (top_left...), style}; " +
                "insert {block, position, scale, rotation, attributes {TAG: value}, block_file (a .dwg imported as the block when missing)}; " +
                "dim_aligned {p1, p2, dim_point, text, style}; dim_linear {p1, p2, dim_point, rotation (0 horizontal, 90 vertical), text, style}; " +
                "hatch {points (closed polygon) or boundary_handles, pattern (SOLID, ANSI31...), scale, angle}; spline {points}. " +
                "Angles in degrees CCW; height defaults to TEXTSIZE.",
                ObjR("items", Space,
                    P("items", Arr("Entities to create", "{'type':'object','properties':{'type':{'type':'string','enum':['line','polyline','rectangle','circle','arc','ellipse','point','text','mtext','insert','dim_aligned','dim_linear','hatch','spline']}},'required':['type']}"))),
                AddEntities);

            Registry.Add("update_entities", "Modify entities", ToolKind.Write,
                "Change properties of entities. Either handles + set (same change for all) or items [{handle, ...props}]. " +
                "Props: layer, color, linetype, lineweight, text, height, rotation, position, start, end, center, radius, closed, width, scale, attributes {TAG: value}. " +
                "Props that do not apply to an entity type are reported in skipped.",
                Obj(Handles,
                    P("set", ObjAny("Properties to apply to every handle")),
                    P("items", Arr("Per-entity changes", "{'type':'object','properties':{'handle':{'type':'string'}},'required':['handle']}"))),
                UpdateEntities);

            Registry.Add("delete_entities", "Delete entities", ToolKind.Write,
                "Erase entities by handle (one undo step).",
                ObjR("handles", Handles), DeleteEntities, destructive: true);

            Registry.Add("transform_entities", "Move / copy / rotate / scale / mirror", ToolKind.Write,
                "Transform entities by handle. operation: move {displacement [dx, dy] or from + to}; rotate {base_point, angle}; " +
                "scale {base_point, factor}; mirror {mirror_line [[x1, y1], [x2, y2]]}. copy=true transforms copies and returns their handles. " +
                "Mirror does not keep text readable (MIRRTEXT is not applied).",
                ObjR("handles,operation", Handles,
                    P("operation", Enum("Transformation", "move", "rotate", "scale", "mirror")),
                    P("copy", Bool("Transform copies instead of the originals")),
                    P("displacement", Pt("move: [dx, dy]")),
                    P("from", Pt("move: base point")),
                    P("to", Pt("move: target point")),
                    P("base_point", Pt("rotate/scale: base point")),
                    P("angle", Num("rotate: degrees CCW")),
                    P("factor", Num("scale: factor")),
                    P("mirror_line", Pts("mirror: two points"))),
                Transform);

            Registry.Add("create_block", "Make block", ToolKind.Write,
                "Create a block definition from existing entities (all from the same space). mode: convert (default) replaces them with a block reference, " +
                "keep leaves them, delete erases them.",
                ObjR("name,handles,base_point", P("name", Str("New block name")), Handles,
                    P("base_point", Pt("Insertion base point")),
                    P("mode", Enum("convert, keep or delete", "convert", "keep", "delete")),
                    P("description", Str("Block description"))),
                CreateBlock);
        }

        // ---------------------------------------------------------------- add_entities

        static object AddEntities(ToolContext c, Args a)
        {
            IList items = a.List("items");
            if (items == null || items.Count == 0) throw new ToolError("'items' must be a non-empty array.");
            ObjectId spaceId = Acad.SpaceId(c.Db, c.Tr, a.Str("space"));
            var space = (BlockTableRecord)c.Tr.GetObject(spaceId, OpenMode.ForWrite);

            var created = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = new Args(items[i]);
                string type = (item.Str("type") ?? "").Trim().ToLowerInvariant();
                try
                {
                    Entity e = Create(c, space, type, item);
                    created.Add(new Dictionary<string, object> { ["index"] = i, ["type"] = type, ["handle"] = e.Handle.ToString() });
                }
                catch (ToolError te) { throw new ToolError($"items[{i}] ({type}): {te.Message} Nothing was created."); }
                catch (AcRx.Exception ae) { throw new ToolError($"items[{i}] ({type}): AutoCAD error {ae.ErrorStatus}{Where(ae)}. Nothing was created."); }
            }
            return new Dictionary<string, object>
            {
                ["space"] = Acad.SpaceName(c.Tr, spaceId),
                ["created_count"] = created.Count,
                ["created"] = created,
            };
        }

        static Entity Create(ToolContext c, BlockTableRecord space, string type, Args it)
        {
            var db = c.Db;
            var tr = c.Tr;
            switch (type)
            {
                case "line":
                    return Add(c, space, new Line(it.ReqPt("start"), it.ReqPt("end")), it);

                case "polyline":
                {
                    List<Point3d> pts = it.Pts("points");
                    if (pts.Count < 2) throw new ToolError("A polyline needs at least 2 points.");
                    List<double> bulges = it.Has("bulges") ? it.List("bulges").Cast<object>().Select(o => Args.ToDouble(o, "bulges")).ToList() : null;
                    if (bulges != null && bulges.Count != pts.Count) throw new ToolError("bulges must have one value per point.");
                    var pl = new Polyline();
                    for (int i = 0; i < pts.Count; i++) pl.AddVertexAt(i, new Point2d(pts[i].X, pts[i].Y), bulges?[i] ?? 0, 0, 0);
                    pl.Closed = it.Bool("closed", false);
                    if (it.Has("width")) pl.ConstantWidth = it.ReqNum("width");
                    if (Math.Abs(pts[0].Z) > 1e-9) pl.Elevation = pts[0].Z;
                    return Add(c, space, pl, it);
                }

                case "rectangle":
                {
                    Point3d p1 = it.ReqPt("corner1");
                    Point3d p2 = it.Has("corner2")
                        ? it.ReqPt("corner2")
                        : new Point3d(p1.X + it.ReqNum("width"), p1.Y + it.ReqNum("height"), p1.Z);
                    var pl = new Polyline();
                    pl.AddVertexAt(0, new Point2d(p1.X, p1.Y), 0, 0, 0);
                    pl.AddVertexAt(1, new Point2d(p2.X, p1.Y), 0, 0, 0);
                    pl.AddVertexAt(2, new Point2d(p2.X, p2.Y), 0, 0, 0);
                    pl.AddVertexAt(3, new Point2d(p1.X, p2.Y), 0, 0, 0);
                    pl.Closed = true;
                    return Add(c, space, pl, it);
                }

                case "circle":
                    return Add(c, space, new Circle(it.ReqPt("center"), Vector3d.ZAxis, Positive(it.ReqNum("radius"), "radius")), it);

                case "arc":
                {
                    if (it.Has("points"))
                    {
                        List<Point3d> p = it.Pts("points");
                        if (p.Count != 3) throw new ToolError("A 3-point arc needs exactly 3 points.");
                        var ca = new CircularArc3d(p[0], p[1], p[2]);
                        double a0 = Vector3d.XAxis.GetAngleTo(p[0] - ca.Center, Vector3d.ZAxis);
                        double a2 = Vector3d.XAxis.GetAngleTo(p[2] - ca.Center, Vector3d.ZAxis);
                        bool ccw = ca.Normal.Z >= 0;
                        return Add(c, space, new Arc(ca.Center, ca.Radius, ccw ? a0 : a2, ccw ? a2 : a0), it);
                    }
                    return Add(c, space, new Arc(it.ReqPt("center"), Positive(it.ReqNum("radius"), "radius"),
                        Rad(it.ReqNum("start_angle")), Rad(it.ReqNum("end_angle"))), it);
                }

                case "ellipse":
                {
                    Point3d axis = it.ReqPt("major_axis");
                    double ratio = it.Num("ratio", 0.5);
                    if (ratio <= 0 || ratio > 1) throw new ToolError("ratio must be in (0, 1].");
                    return Add(c, space, new Ellipse(it.ReqPt("center"), Vector3d.ZAxis, new Vector3d(axis.X, axis.Y, 0), ratio, 0, 2 * Math.PI), it);
                }

                case "point":
                    return Add(c, space, new DBPoint(it.ReqPt("position")), it);

                case "text":
                {
                    Point3d at = it.ReqPt("position");
                    var t = new DBText();
                    t.SetDatabaseDefaults(db);
                    t.TextString = it.ReqStr("text");
                    t.Height = Positive(it.Num("height", db.Textsize), "height");
                    t.Rotation = Rad(it.Num("rotation", 0));
                    t.TextStyleId = Acad.TextStyle(db, tr, it.Str("style"));
                    t.Position = at;
                    bool justified = SetJustification(t, it.Str("align"));
                    if (justified) t.AlignmentPoint = at;
                    Add(c, space, t, it);
                    if (justified) t.AdjustAlignment(db);
                    return t;
                }

                case "mtext":
                {
                    var m = new MText();
                    m.SetDatabaseDefaults(db);
                    m.Location = it.ReqPt("position");
                    m.Contents = MTextContents(it.ReqStr("text"));
                    m.TextHeight = Positive(it.Num("height", db.Textsize), "height");
                    m.Width = it.Num("width", 0);
                    m.Rotation = Rad(it.Num("rotation", 0));
                    m.TextStyleId = Acad.TextStyle(db, tr, it.Str("style"));
                    m.Attachment = Attachment(it.Str("attachment"));
                    return Add(c, space, m, it);
                }

                case "insert":
                    return Insert(c, space, it);

                case "dim_aligned":
                    return Add(c, space, new AlignedDimension(it.ReqPt("p1"), it.ReqPt("p2"), it.ReqPt("dim_point"),
                        it.Str("text") ?? "", Acad.DimStyle(db, tr, it.Str("style"))), it);

                case "dim_linear":
                    return Add(c, space, new RotatedDimension(Rad(it.Num("rotation", 0)), it.ReqPt("p1"), it.ReqPt("p2"), it.ReqPt("dim_point"),
                        it.Str("text") ?? "", Acad.DimStyle(db, tr, it.Str("style"))), it);

                case "hatch":
                    return HatchOf(c, space, it);

                case "spline":
                {
                    List<Point3d> pts = it.Pts("points");
                    if (pts.Count < 2) throw new ToolError("A spline needs at least 2 points.");
                    return Add(c, space, new Spline(new Point3dCollection(pts.ToArray()), 4, 0.0), it);
                }

                default:
                    throw new ToolError("Unknown type. Use line, polyline, rectangle, circle, arc, ellipse, point, text, mtext, insert, dim_aligned, dim_linear, hatch or spline.");
            }
        }

        /// <summary>" (at EditTools.cs:123)" for the first add-in frame, so AutoCAD errors point at the failing step.</summary>
        static string Where(Exception ex)
        {
            foreach (var frame in new System.Diagnostics.StackTrace(ex, true).GetFrames() ?? new System.Diagnostics.StackFrame[0])
            {
                string file = frame.GetFileName();
                if (file != null && frame.GetFileLineNumber() > 0)
                    return $" (at {Path.GetFileName(file)}:{frame.GetFileLineNumber()})";
            }
            return "";
        }

        static Entity Add(ToolContext c, BlockTableRecord space, Entity e, Args it)
        {
            if (!(e is DBText) && !(e is MText)) e.SetDatabaseDefaults(c.Db);
            foreach (string key in CommonKeys)
                if (it.Has(key)) Acad.ApplyCommon(e, key, it.Raw(key), c.Db, c.Tr);
            Acad.Append(space, e, c.Tr);
            return e;
        }

        static double Positive(double v, string name)
        {
            if (v <= 0) throw new ToolError($"{name} must be greater than 0.");
            return v;
        }

        static string MTextContents(string text) => text.Replace("\r\n", "\\P").Replace("\n", "\\P");

        /// <summary>Returns true when the text is not left/baseline justified (AlignmentPoint is used).</summary>
        static bool SetJustification(DBText t, string align)
        {
            string a = (align ?? "left").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            TextHorizontalMode h;
            TextVerticalMode v;
            switch (a)
            {
                case "left": return false;
                case "center": h = TextHorizontalMode.TextCenter; v = TextVerticalMode.TextBase; break;
                case "right": h = TextHorizontalMode.TextRight; v = TextVerticalMode.TextBase; break;
                case "middle": h = TextHorizontalMode.TextMid; v = TextVerticalMode.TextBase; break;
                case "top_left": h = TextHorizontalMode.TextLeft; v = TextVerticalMode.TextTop; break;
                case "top_center": h = TextHorizontalMode.TextCenter; v = TextVerticalMode.TextTop; break;
                case "top_right": h = TextHorizontalMode.TextRight; v = TextVerticalMode.TextTop; break;
                case "middle_left": h = TextHorizontalMode.TextLeft; v = TextVerticalMode.TextVerticalMid; break;
                case "middle_center": h = TextHorizontalMode.TextCenter; v = TextVerticalMode.TextVerticalMid; break;
                case "middle_right": h = TextHorizontalMode.TextRight; v = TextVerticalMode.TextVerticalMid; break;
                case "bottom_left": h = TextHorizontalMode.TextLeft; v = TextVerticalMode.TextBottom; break;
                case "bottom_center": h = TextHorizontalMode.TextCenter; v = TextVerticalMode.TextBottom; break;
                case "bottom_right": h = TextHorizontalMode.TextRight; v = TextVerticalMode.TextBottom; break;
                default: throw new ToolError($"Unknown align '{align}'.");
            }
            t.HorizontalMode = h;
            t.VerticalMode = v;
            return true;
        }

        static AttachmentPoint Attachment(string name)
        {
            switch ((name ?? "top_left").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_'))
            {
                case "top_left": return AttachmentPoint.TopLeft;
                case "top_center": return AttachmentPoint.TopCenter;
                case "top_right": return AttachmentPoint.TopRight;
                case "middle_left": return AttachmentPoint.MiddleLeft;
                case "middle_center": return AttachmentPoint.MiddleCenter;
                case "middle_right": return AttachmentPoint.MiddleRight;
                case "bottom_left": return AttachmentPoint.BottomLeft;
                case "bottom_center": return AttachmentPoint.BottomCenter;
                case "bottom_right": return AttachmentPoint.BottomRight;
                default: throw new ToolError($"Unknown attachment '{name}'.");
            }
        }

        static Entity Insert(ToolContext c, BlockTableRecord space, Args it)
        {
            var db = c.Db;
            var tr = c.Tr;
            string name = it.Str("block");
            string file = it.Str("block_file");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(file)) throw new ToolError("Pass block (a block name) or block_file (a .dwg).");
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(file);

            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!table.Has(name) && !string.IsNullOrEmpty(file))
            {
                if (!File.Exists(file)) throw new ToolError($"block_file not found: {file}");
                Acad.ValidateName(name, "block");
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
                    db.Insert(name, source, true);
                }
                table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            }
            if (!table.Has(name)) throw new ToolError($"Block '{name}' does not exist. See list_blocks, or pass block_file to import a .dwg.");
            ObjectId btrId = table[name];

            var br = new BlockReference(it.ReqPt("position"), btrId);
            br.SetDatabaseDefaults(db);
            br.Rotation = Rad(it.Num("rotation", 0));
            br.ScaleFactors = ScaleOf(it.Raw("scale"));
            Add(c, space, br, it);

            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            if (btr.HasAttributeDefinitions)
            {
                Dictionary<string, string> values = Values(it.Raw("attributes"));
                foreach (ObjectId id in btr)
                {
                    if (id.ObjectClass.DxfName != "ATTDEF") continue;
                    var ad = (AttributeDefinition)tr.GetObject(id, OpenMode.ForRead);
                    if (ad.Constant) continue;
                    var ar = new AttributeReference();
                    ar.SetAttributeFromBlock(ad, br.BlockTransform);
                    if (values.TryGetValue(ad.Tag, out string value)) ar.TextString = value;
                    if (ar.IsMTextAttribute) ar.UpdateMTextAttribute();
                    br.AttributeCollection.AppendAttribute(ar);
                    tr.AddNewlyCreatedDBObject(ar, true);
                }
            }
            return br;
        }

        static Scale3d ScaleOf(object raw)
        {
            if (raw == null) return new Scale3d(1);
            if (raw is IList l && !(raw is string))
            {
                if (l.Count < 2) throw new ToolError("scale must be a number or [sx, sy] / [sx, sy, sz].");
                double sx = Args.ToDouble(l[0], "scale"), sy = Args.ToDouble(l[1], "scale");
                return new Scale3d(sx, sy, l.Count > 2 ? Args.ToDouble(l[2], "scale") : sx);
            }
            double s = Args.ToDouble(raw, "scale");
            if (Math.Abs(s) < 1e-12) throw new ToolError("scale cannot be 0.");
            return new Scale3d(s);
        }

        static Dictionary<string, string> Values(object raw)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (raw is Dictionary<string, object> d)
                foreach (var kv in d) values[kv.Key] = Convert.ToString(kv.Value ?? "");
            return values;
        }

        static Entity HatchOf(ToolContext c, BlockTableRecord space, Args it)
        {
            var h = new Hatch();
            Add(c, space, h, it);
            // scale/angle on a hatch without a pattern -> eInvalidInput: set the pattern first,
            // then re-apply it so the new scale/angle are used.
            string pattern = (it.Str("pattern") ?? "SOLID").ToUpperInvariant();
            h.SetHatchPattern(HatchPatternType.PreDefined, pattern);
            double scale = Positive(it.Num("scale", 1), "scale"), angle = it.Num("angle", 0);
            if (Math.Abs(scale - 1) > 1e-12 || Math.Abs(angle) > 1e-12)
            {
                h.PatternScale = scale;
                h.PatternAngle = Rad(angle);
                h.SetHatchPattern(HatchPatternType.PreDefined, pattern);
            }
            if (it.Has("boundary_handles"))
            {
                var ids = new ObjectIdCollection(Acad.Ids(c.Db, it.Strs("boundary_handles")).ToArray());
                h.Associative = true;
                h.AppendLoop(HatchLoopTypes.Outermost, ids);
                h.EvaluateHatch(true);
                return h;
            }

            // polygon: hatch a temporary closed polyline, then erase it (the hatch keeps its own loop)
            List<Point3d> pts = it.Pts("points");
            if (pts.Count < 3) throw new ToolError("A hatch polygon needs at least 3 points (or pass boundary_handles).");
            var boundary = new Polyline();
            foreach (Point3d p in pts) boundary.AddVertexAt(boundary.NumberOfVertices, new Point2d(p.X, p.Y), 0, 0, 0);
            if (boundary.GetPoint2dAt(0).IsEqualTo(boundary.GetPoint2dAt(boundary.NumberOfVertices - 1)))
                boundary.RemoveVertexAt(boundary.NumberOfVertices - 1);
            boundary.Closed = true;
            boundary.SetDatabaseDefaults(c.Db);
            Acad.Append(space, boundary, c.Tr);
            h.Associative = false;
            h.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { boundary.ObjectId });
            h.EvaluateHatch(true);
            boundary.Erase();
            return h;
        }

        // ---------------------------------------------------------------- update / delete

        static object UpdateEntities(ToolContext c, Args a)
        {
            var work = new List<KeyValuePair<string, Args>>();
            if (a.Has("items"))
            {
                foreach (object o in a.List("items"))
                {
                    var item = new Args(o);
                    work.Add(new KeyValuePair<string, Args>(item.ReqStr("handle"), item));
                }
            }
            if (a.Has("handles"))
            {
                Args set = a.Obj("set");
                if (!set.Keys.Any()) throw new ToolError("With handles, pass the changes in 'set'.");
                foreach (string h in a.Strs("handles")) work.Add(new KeyValuePair<string, Args>(h, set));
            }
            if (work.Count == 0) throw new ToolError("Pass items [{handle, ...}] or handles + set.");

            var results = new List<object>();
            int updated = 0;
            foreach (var job in work)
            {
                var r = new Dictionary<string, object> { ["handle"] = job.Key };
                try
                {
                    var e = (Entity)c.Tr.GetObject(Acad.Id(c.Db, job.Key), OpenMode.ForWrite);
                    List<string> skipped = Apply(c, e, job.Value);
                    if (skipped.Count > 0) r["skipped"] = skipped;
                    r["ok"] = true;
                    updated++;
                }
                catch (ToolError te) { r["ok"] = false; r["error"] = te.Message; }
                catch (AcRx.Exception ae)
                {
                    r["ok"] = false;
                    r["error"] = ae.ErrorStatus == AcRx.ErrorStatus.OnLockedLayer ? "entity is on a locked layer" : "AutoCAD error " + ae.ErrorStatus;
                }
                catch (InvalidCastException) { r["ok"] = false; r["error"] = "not an entity"; }
                results.Add(r);
            }
            return new Dictionary<string, object> { ["updated"] = updated, ["results"] = results };
        }

        static List<string> Apply(ToolContext c, Entity e, Args s)
        {
            var skipped = new List<string>();
            foreach (string rawKey in s.Keys.ToList())
            {
                string key = rawKey.ToLowerInvariant();
                if (key == "handle" || s.Raw(rawKey) == null) continue;
                if (Acad.ApplyCommon(e, key, s.Raw(rawKey), c.Db, c.Tr)) continue;
                if (!ApplyGeometry(c, e, key, s, rawKey)) skipped.Add(rawKey);
            }
            return skipped;
        }

        static bool ApplyGeometry(ToolContext c, Entity e, string key, Args s, string rawKey)
        {
            switch (key)
            {
                case "text":
                    string text = s.Str(rawKey);
                    switch (e)
                    {
                        case DBText t: t.TextString = text; return true;
                        case MText m: m.Contents = MTextContents(text); return true;
                        case Dimension d: d.DimensionText = text; return true;
                        case MLeader ml when ml.ContentType == ContentType.MTextContent:
                            MText copy = ml.MText;
                            copy.Contents = MTextContents(text);
                            ml.MText = copy;
                            return true;
                    }
                    return false;

                case "height":
                    double height = Positive(s.ReqNum(rawKey), "height");
                    if (e is DBText th) { th.Height = height; return true; }
                    if (e is MText mh) { mh.TextHeight = height; return true; }
                    return false;

                case "width":
                    if (e is MText mw) { mw.Width = s.ReqNum(rawKey); return true; }
                    if (e is Polyline pw) { pw.ConstantWidth = s.ReqNum(rawKey); return true; }
                    return false;

                case "rotation":
                    double rad = Rad(s.ReqNum(rawKey));
                    switch (e)
                    {
                        case DBText t: t.Rotation = rad; return true;
                        case MText m: m.Rotation = rad; return true;
                        case BlockReference br: br.TransformBy(Matrix3d.Rotation(rad - br.Rotation, br.Normal, br.Position)); return true;
                    }
                    return false;

                case "position":
                    Point3d at = s.ReqPt(rawKey);
                    switch (e)
                    {
                        case DBText t:
                            if (Acad.IsDefaultJustified(t)) t.Position = at;
                            else { t.AlignmentPoint = at; t.AdjustAlignment(c.Db); }
                            return true;
                        case MText m: m.Location = at; return true;
                        case BlockReference br: br.TransformBy(Matrix3d.Displacement(at - br.Position)); return true;
                        case DBPoint p: p.Position = at; return true;
                    }
                    return false;

                case "start":
                    if (e is Line ls) { ls.StartPoint = s.ReqPt(rawKey); return true; }
                    return false;

                case "end":
                    if (e is Line le) { le.EndPoint = s.ReqPt(rawKey); return true; }
                    return false;

                case "center":
                    Point3d center = s.ReqPt(rawKey);
                    if (e is Circle cc) { cc.Center = center; return true; }
                    if (e is Arc ac) { ac.Center = center; return true; }
                    if (e is Ellipse ec) { ec.Center = center; return true; }
                    return false;

                case "radius":
                    double radius = Positive(s.ReqNum(rawKey), "radius");
                    if (e is Circle cr) { cr.Radius = radius; return true; }
                    if (e is Arc ar) { ar.Radius = radius; return true; }
                    return false;

                case "closed":
                    if (e is Polyline pc) { pc.Closed = s.Bool(rawKey, pc.Closed); return true; }
                    return false;

                case "scale":
                    if (!(e is BlockReference bs)) return false;
                    Scale3d target = ScaleOf(s.Raw(rawKey));
                    Scale3d current = bs.ScaleFactors;
                    bool uniform = Math.Abs(target.X - target.Y) < 1e-12 && Math.Abs(target.X - target.Z) < 1e-12 &&
                                   Math.Abs(current.X - current.Y) < 1e-12 && Math.Abs(current.X - current.Z) < 1e-12;
                    if (uniform) bs.TransformBy(Matrix3d.Scaling(target.X / current.X, bs.Position)); // keeps attributes in place
                    else bs.ScaleFactors = target;
                    return true;

                case "attributes":
                    if (!(e is BlockReference ba)) return false;
                    Dictionary<string, string> values = Values(s.Raw(rawKey));
                    var attrs = ba.AttributeCollection.Cast<ObjectId>()
                        .Select(aid => (AttributeReference)c.Tr.GetObject(aid, OpenMode.ForRead)).ToList();
                    var unknown = values.Keys.Where(k => !attrs.Any(x => x.Tag.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (unknown.Count > 0)
                        throw new ToolError("Block has no attribute(s): " + string.Join(", ", unknown) + ". Existing tags: " + string.Join(", ", attrs.Select(x => x.Tag)));
                    foreach (AttributeReference attr in attrs)
                    {
                        if (!values.TryGetValue(attr.Tag, out string value)) continue;
                        attr.UpgradeOpen();
                        attr.TextString = value;
                        if (attr.IsMTextAttribute) attr.UpdateMTextAttribute();
                    }
                    return true;

                default:
                    return false;
            }
        }

        static object DeleteEntities(ToolContext c, Args a)
        {
            var errors = new List<object>();
            int erased = 0;
            foreach (string h in a.Strs("handles"))
            {
                try
                {
                    c.Tr.GetObject(Acad.Id(c.Db, h), OpenMode.ForWrite).Erase();
                    erased++;
                }
                catch (ToolError te) { errors.Add(new Dictionary<string, object> { ["handle"] = h, ["error"] = te.Message }); }
                catch (AcRx.Exception ae) { errors.Add(new Dictionary<string, object> { ["handle"] = h, ["error"] = "AutoCAD error " + ae.ErrorStatus }); }
            }
            var result = new Dictionary<string, object> { ["erased"] = erased };
            if (errors.Count > 0) result["errors"] = errors;
            return result;
        }

        // ---------------------------------------------------------------- transform / block

        static object Transform(ToolContext c, Args a)
        {
            List<ObjectId> ids = Acad.Ids(c.Db, a.Strs("handles"));
            Matrix3d m = MatrixOf(a);

            var newHandles = new List<object>();
            if (a.Bool("copy", false))
            {
                foreach (var group in ids.GroupBy(id => c.Tr.GetObject(id, OpenMode.ForRead).OwnerId))
                {
                    var map = new IdMapping();
                    c.Db.DeepCloneObjects(new ObjectIdCollection(group.ToArray()), group.Key, map, false);
                    foreach (IdPair pair in map)
                    {
                        if (!pair.IsPrimary || !pair.IsCloned) continue;
                        if (c.Tr.GetObject(pair.Value, OpenMode.ForWrite) is Entity copy)
                        {
                            copy.TransformBy(m);
                            newHandles.Add(copy.Handle.ToString());
                        }
                    }
                }
                return new Dictionary<string, object> { ["copied"] = newHandles.Count, ["new_handles"] = newHandles };
            }

            foreach (ObjectId id in ids) ((Entity)c.Tr.GetObject(id, OpenMode.ForWrite)).TransformBy(m);
            return new Dictionary<string, object> { ["transformed"] = ids.Count };
        }

        static Matrix3d MatrixOf(Args a)
        {
            switch ((a.Str("operation") ?? "").ToLowerInvariant())
            {
                case "move":
                    if (a.Has("displacement"))
                    {
                        Point3d d = a.ReqPt("displacement");
                        return Matrix3d.Displacement(new Vector3d(d.X, d.Y, d.Z));
                    }
                    return Matrix3d.Displacement(a.ReqPt("to") - a.ReqPt("from"));
                case "rotate":
                    return Matrix3d.Rotation(Rad(a.ReqNum("angle")), Vector3d.ZAxis, a.ReqPt("base_point"));
                case "scale":
                    return Matrix3d.Scaling(Positive(a.ReqNum("factor"), "factor"), a.ReqPt("base_point"));
                case "mirror":
                    List<Point3d> line = a.Pts("mirror_line");
                    if (line.Count != 2 || line[0].IsEqualTo(line[1])) throw new ToolError("mirror_line must be two different points.");
                    return Matrix3d.Mirroring(new Line3d(line[0], line[1]));
                default:
                    throw new ToolError("operation must be move, rotate, scale or mirror.");
            }
        }

        static object CreateBlock(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string name = a.ReqStr("name");
            Acad.ValidateName(name, "block");
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (table.Has(name)) throw new ToolError($"A block named '{name}' already exists.");

            List<ObjectId> ids = Acad.Ids(db, a.Strs("handles"));
            ObjectId owner = tr.GetObject(ids[0], OpenMode.ForRead).OwnerId;
            if (ids.Any(id => tr.GetObject(id, OpenMode.ForRead).OwnerId != owner))
                throw new ToolError("All entities must be in the same space.");
            Point3d basePoint = a.ReqPt("base_point");
            string mode = (a.Str("mode") ?? "convert").ToLowerInvariant();
            if (mode != "convert" && mode != "keep" && mode != "delete") throw new ToolError("mode must be convert, keep or delete.");

            table.UpgradeOpen();
            var btr = new BlockTableRecord { Name = name, Origin = basePoint, Comments = a.Str("description") ?? "" };
            ObjectId btrId = table.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            db.DeepCloneObjects(new ObjectIdCollection(ids.ToArray()), btrId, new IdMapping(), false);

            string insertHandle = null;
            if (mode != "keep")
            {
                foreach (ObjectId id in ids) tr.GetObject(id, OpenMode.ForWrite).Erase();
            }
            if (mode == "convert")
            {
                var space = (BlockTableRecord)tr.GetObject(owner, OpenMode.ForWrite);
                var br = new BlockReference(basePoint, btrId);
                br.SetDatabaseDefaults(db);
                Acad.Append(space, br, tr);
                insertHandle = br.Handle.ToString();
            }
            return new Dictionary<string, object>
            {
                ["block"] = name,
                ["entities"] = ids.Count,
                ["mode"] = mode,
                ["insert_handle"] = insertHandle,
            };
        }
    }
}
