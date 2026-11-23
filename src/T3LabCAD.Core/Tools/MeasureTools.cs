using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>Measuring and data extraction: measure, quantity_takeoff, extract_block_data.</summary>
    internal static class MeasureTools
    {
        public static void Register()
        {
            Registry.Add("measure", "Measure", ToolKind.Read,
                "Measure in drawing units (WCS). mode: distance {points: chain of 2+ points}; angle {points [vertex, p1, p2] or handles of 2 lines}; " +
                "area {points polygon, or handles of closed curves / hatches / regions}; length {handles}; bbox {handles}; " +
                "closest {handles [curve], point}; intersections {handles (2+), extend}.",
                ObjR("mode",
                    P("mode", Enum("What to measure", "distance", "angle", "area", "length", "bbox", "closest", "intersections")),
                    P("points", Pts("Points [[x, y], ...]")),
                    Handles,
                    P("point", Pt("closest: the point to project")),
                    P("extend", Bool("intersections: also where the extended curves would meet (default false)"))),
                Measure);

            Registry.Add("quantity_takeoff", "Quantity takeoff", ToolKind.Read,
                "Count, total length and total area of entities grouped by layer, type and/or block, like a bill of quantities. " +
                "Length counts curves (lines, polylines, arcs, circles, splines...), area counts closed curves, hatches and regions. " +
                "length_factor / area_factor convert units (drawing in mm: 0.001 and 0.000001 give m and m2). csv_path also writes a CSV.",
                Obj(P("space", Str("model (default), paper, current, a layout name, or all")),
                    P("types", Strs("DXF type names, wildcards allowed")),
                    P("layers", Strs("Layer names, wildcards allowed")),
                    P("block_names", Strs("For INSERT: block names, wildcards allowed")),
                    P("group_by", Enum("Grouping (default layer_type)", "layer", "type", "block", "layer_type", "layer_block")),
                    P("length_factor", Num("Multiply lengths by this (default 1)")),
                    P("area_factor", Num("Multiply areas by this (default 1)")),
                    P("csv_path", Str("Also write the table to this .csv (UTF-8, opens in Excel)")),
                    P("delimiter", Enum("CSV separator (default ,; use ; for Excel with comma decimals)", ",", ";"))),
                Takeoff, writesFiles: true);

            Registry.Add("extract_block_data", "Extract block data", ToolKind.Read,
                "One row per block insert: block, layer, layout, position, rotation, scale, every attribute value (by tag) and dynamic " +
                "property (dyn:NAME, angles in degrees). Like DATAEXTRACTION; csv_path also writes a CSV for Excel.",
                Obj(P("block_names", Strs("Block names, wildcards allowed (default all blocks, xrefs excluded)")),
                    P("space", Str("all (default), model, paper, current or a layout name")),
                    P("tags", Strs("Only these attribute tags (default all)")),
                    P("dynamic", Bool("Include dynamic block properties (default true)")),
                    P("csv_path", Str("Also write the rows to this .csv (UTF-8, opens in Excel)")),
                    P("delimiter", Enum("CSV separator (default ,; use ; for Excel with comma decimals)", ",", ";")),
                    P("limit", Int("Max rows returned in the result (default 500; the CSV gets all)"))),
                ExtractBlockData, writesFiles: true);
        }

        // ---------------------------------------------------------------- measure

        static object Measure(ToolContext c, Args a)
        {
            string mode = a.ReqStr("mode").Trim().ToLowerInvariant();
            var tr = c.Tr;
            var result = new Dictionary<string, object> { ["mode"] = mode };
            switch (mode)
            {
                case "distance":
                {
                    List<Point3d> pts = a.Pts("points");
                    if (pts.Count < 2) throw new ToolError("distance needs at least 2 points.");
                    double total = 0;
                    var segments = new List<object>();
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        Vector3d v = pts[i + 1] - pts[i];
                        total += v.Length;
                        if (segments.Count < 100)
                            segments.Add(new Dictionary<string, object>
                            {
                                ["from"] = Geo.P(pts[i]),
                                ["to"] = Geo.P(pts[i + 1]),
                                ["length"] = Geo.R(v.Length),
                                ["dx"] = Geo.R(v.X),
                                ["dy"] = Geo.R(v.Y),
                                ["angle"] = Geo.R(Normalize(Math.Atan2(v.Y, v.X)) * 180 / Math.PI),
                            });
                    }
                    result["total"] = Geo.R(total);
                    result["segments"] = segments;
                    return result;
                }

                case "angle":
                {
                    Vector3d v1, v2;
                    if (a.Has("handles"))
                    {
                        List<ObjectId> ids = Acad.Ids(c.Db, a.Strs("handles"));
                        if (ids.Count != 2) throw new ToolError("angle with handles needs exactly 2 lines.");
                        v1 = Direction(tr, ids[0]);
                        v2 = Direction(tr, ids[1]);
                    }
                    else
                    {
                        List<Point3d> pts = a.Pts("points");
                        if (pts.Count != 3) throw new ToolError("angle needs points [vertex, p1, p2] or 2 line handles.");
                        v1 = pts[1] - pts[0];
                        v2 = pts[2] - pts[0];
                    }
                    if (v1.Length < 1e-12 || v2.Length < 1e-12) throw new ToolError("The angle is undefined for zero-length directions.");
                    double between = v1.GetAngleTo(v2);
                    result["angle"] = Geo.Deg(between);
                    result["acute"] = Geo.Deg(Math.Min(between, Math.PI - between));
                    result["ccw_from_first"] = Geo.Deg(v1.GetAngleTo(v2, Vector3d.ZAxis));
                    return result;
                }

                case "area":
                    if (!a.Has("handles"))
                    {
                        List<Point3d> pts = a.Pts("points");
                        if (pts.Count < 3) throw new ToolError("area needs a polygon of 3+ points, or handles.");
                        double area2 = 0, perimeter = 0;
                        for (int i = 0; i < pts.Count; i++)
                        {
                            Point3d p = pts[i], q = pts[(i + 1) % pts.Count];
                            area2 += p.X * q.Y - q.X * p.Y;
                            perimeter += p.DistanceTo(q);
                        }
                        result["area"] = Geo.R(Math.Abs(area2) / 2);
                        result["perimeter"] = Geo.R(perimeter);
                        return result;
                    }
                    return PerEntity(c, a, result, true);

                case "length":
                    return PerEntity(c, a, result, false);

                case "bbox":
                {
                    var ext = new Extents3d();
                    bool any = false;
                    foreach (ObjectId id in Acad.Ids(c.Db, a.Strs("handles")))
                    {
                        Extents3d? b = (tr.GetObject(id, OpenMode.ForRead) as Entity)?.Bounds;
                        if (!b.HasValue) continue;
                        if (any) ext.AddExtents(b.Value); else ext = b.Value;
                        any = true;
                    }
                    if (!any) throw new ToolError("None of these entities has an extent.");
                    result["min"] = Geo.P(ext.MinPoint);
                    result["max"] = Geo.P(ext.MaxPoint);
                    result["width"] = Geo.R(ext.MaxPoint.X - ext.MinPoint.X);
                    result["height"] = Geo.R(ext.MaxPoint.Y - ext.MinPoint.Y);
                    result["center"] = Geo.P(ext.MinPoint + (ext.MaxPoint - ext.MinPoint) / 2);
                    return result;
                }

                case "closest":
                {
                    List<ObjectId> ids = Acad.Ids(c.Db, a.Strs("handles"));
                    if (!(tr.GetObject(ids[0], OpenMode.ForRead) is Curve curve)) throw new ToolError("closest needs a curve (line, polyline, arc, circle...).");
                    Point3d from = a.ReqPt("point");
                    Point3d on = curve.GetClosestPointTo(from, false);
                    result["point"] = Geo.P(on);
                    result["distance"] = Geo.R(on.DistanceTo(from));
                    try { result["distance_along_curve"] = Geo.R(curve.GetDistAtPoint(on)); } catch (AcRx.Exception) { }
                    return result;
                }

                case "intersections":
                {
                    List<ObjectId> ids = Acad.Ids(c.Db, a.Strs("handles"));
                    if (ids.Count < 2) throw new ToolError("intersections needs at least 2 handles.");
                    if (ids.Count > 60) throw new ToolError("intersections takes at most 60 handles per call.");
                    Intersect how = a.Bool("extend", false) ? Intersect.ExtendBoth : Intersect.OnBothOperands;
                    var entities = ids.Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).ToList();
                    var pairs = new List<object>();
                    int count = 0;
                    for (int i = 0; i < entities.Count; i++)
                    {
                        for (int j = i + 1; j < entities.Count; j++)
                        {
                            var pts = new Point3dCollection();
                            try { entities[i].IntersectWith(entities[j], how, pts, IntPtr.Zero, IntPtr.Zero); }
                            catch (AcRx.Exception) { continue; }
                            if (pts.Count == 0) continue;
                            count += pts.Count;
                            pairs.Add(new Dictionary<string, object>
                            {
                                ["a"] = entities[i].Handle.ToString(),
                                ["b"] = entities[j].Handle.ToString(),
                                ["points"] = pts.Cast<Point3d>().Select(p => Geo.P(p)).ToList(),
                            });
                        }
                    }
                    result["count"] = count;
                    result["pairs"] = pairs;
                    return result;
                }

                default:
                    throw new ToolError("mode must be distance, angle, area, length, bbox, closest or intersections.");
            }
        }

        static double Normalize(double rad)
        {
            double twoPi = 2 * Math.PI;
            rad %= twoPi;
            return rad < 0 ? rad + twoPi : rad;
        }

        static Vector3d Direction(Transaction tr, ObjectId id)
        {
            switch (tr.GetObject(id, OpenMode.ForRead))
            {
                case Line l: return l.EndPoint - l.StartPoint;
                case Xline x: return x.UnitDir;
                case Ray r: return r.UnitDir;
                case Polyline pl when pl.NumberOfVertices == 2: return pl.GetPoint3dAt(1) - pl.GetPoint3dAt(0);
                default: throw new ToolError($"{id.Handle} is not a straight line.");
            }
        }

        static object PerEntity(ToolContext c, Args a, Dictionary<string, object> result, bool area)
        {
            var items = new List<object>();
            var skipped = new List<object>();
            double total = 0;
            foreach (ObjectId id in Acad.Ids(c.Db, a.Strs("handles")))
            {
                var e = (Entity)c.Tr.GetObject(id, OpenMode.ForRead);
                double? value = area ? Metrics.Area(e) : Metrics.Length(e);
                if (!value.HasValue)
                {
                    skipped.Add(e.Handle.ToString());
                    continue;
                }
                total += value.Value;
                var item = new Dictionary<string, object>
                {
                    ["handle"] = e.Handle.ToString(),
                    ["type"] = e.GetRXClass().DxfName,
                    [area ? "area" : "length"] = Geo.R(value.Value),
                };
                if (area)
                {
                    double? length = Metrics.Length(e);
                    if (length.HasValue) item["perimeter"] = Geo.R(length.Value);
                }
                items.Add(item);
            }
            result["total"] = Geo.R(total);
            result["entities"] = items;
            if (skipped.Count > 0) result[area ? "not_closed_or_no_area" : "no_length"] = skipped;
            return result;
        }

        // ---------------------------------------------------------------- quantity_takeoff

        sealed class Quantity
        {
            public int Count, Measured, WithArea;
            public double Length, Area;
        }

        static object Takeoff(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string space = a.Str("space") ?? "model";
            List<string> types = a.Strs("types").Select(t => t.ToUpperInvariant()).ToList();
            List<string> layers = a.Strs("layers");
            List<string> blocks = a.Strs("block_names");
            string groupBy = (a.Str("group_by") ?? "layer_type").Trim().ToLowerInvariant();
            string[] keys;
            switch (groupBy)
            {
                case "layer": keys = new[] { "layer" }; break;
                case "type": keys = new[] { "type" }; break;
                case "block": keys = new[] { "block" }; break;
                case "layer_type": keys = new[] { "layer", "type" }; break;
                case "layer_block": keys = new[] { "layer", "block" }; break;
                default: throw new ToolError("group_by must be layer, type, block, layer_type or layer_block.");
            }
            double lf = a.Num("length_factor", 1), af = a.Num("area_factor", 1);

            var groups = new Dictionary<string, Quantity>(StringComparer.OrdinalIgnoreCase);
            var labels = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var total = new Quantity();
            foreach (ObjectId spaceId in Acad.SpaceIds(db, tr, space))
            {
                foreach (ObjectId id in (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead))
                {
                    string dxf = id.ObjectClass.DxfName;
                    if (dxf == "VIEWPORT") continue;
                    if (types.Count > 0 && !Wild.Any(dxf, types)) continue;
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    if (!Wild.Any(e.Layer, layers)) continue;
                    string block = e is BlockReference br ? Acad.EffectiveName(br, tr) : "";
                    if (blocks.Count > 0 && (block.Length == 0 || !Wild.Any(block, blocks))) continue;

                    string[] parts = keys.Select(k => k == "layer" ? e.Layer : k == "type" ? dxf : block).ToArray();
                    string key = string.Join("\u0001", parts);
                    if (!groups.TryGetValue(key, out Quantity q))
                    {
                        groups[key] = q = new Quantity();
                        labels[key] = parts;
                    }
                    double? length = Metrics.Length(e), area = Metrics.Area(e);
                    foreach (Quantity target in new[] { q, total })
                    {
                        target.Count++;
                        if (length.HasValue) { target.Length += length.Value * lf; target.Measured++; }
                        if (area.HasValue) { target.Area += area.Value * af; target.WithArea++; }
                    }
                }
            }

            var ordered = groups.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var rows = new List<object>();
            var csvRows = new List<IList<object>>();
            foreach (var kv in ordered)
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < keys.Length; i++) row[keys[i]] = labels[kv.Key][i];
                row["count"] = kv.Value.Count;
                if (kv.Value.Measured > 0) row["length"] = Geo.R(kv.Value.Length);
                if (kv.Value.WithArea > 0) row["area"] = Geo.R(kv.Value.Area);
                rows.Add(row);
                var csv = new List<object>(labels[kv.Key]) { kv.Value.Count };
                csv.Add(kv.Value.Measured > 0 ? (object)kv.Value.Length : null);
                csv.Add(kv.Value.WithArea > 0 ? (object)kv.Value.Area : null);
                csvRows.Add(csv);
            }

            var result = new Dictionary<string, object>
            {
                ["space"] = space,
                ["group_by"] = groupBy,
                ["insunits"] = db.Insunits.ToString(),
                ["length_factor"] = lf,
                ["area_factor"] = af,
                ["groups"] = rows,
                ["total"] = new Dictionary<string, object>
                {
                    ["count"] = total.Count,
                    ["length"] = Geo.R(total.Length),
                    ["area"] = Geo.R(total.Area),
                },
            };
            if (a.Has("csv_path"))
            {
                var header = new List<string>(keys) { "count", "length", "area" };
                result["csv"] = Csv.Write(a.ReqStr("csv_path"), header, csvRows, a.Str("delimiter"));
            }
            return result;
        }

        // ---------------------------------------------------------------- extract_block_data

        static object ExtractBlockData(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            List<string> names = a.Strs("block_names");
            List<string> tags = a.Strs("tags");
            bool dynamic = a.Bool("dynamic", true);
            int limit = Math.Max(1, Math.Min(5000, a.Int("limit", 500)));
            string space = a.Str("space") ?? "all";

            var tagColumns = new List<string>();
            var dynColumns = new List<string>();
            var records = new List<Dictionary<string, object>>();
            foreach (ObjectId spaceId in Acad.SpaceIds(db, tr, space))
            {
                string spaceName = Acad.SpaceName(tr, spaceId);
                foreach (ObjectId id in (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead))
                {
                    if (id.ObjectClass.DxfName != "INSERT") continue;
                    if (!(tr.GetObject(id, OpenMode.ForRead) is BlockReference br)) continue;
                    if (((BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead)).IsFromExternalReference) continue;
                    string block = Acad.EffectiveName(br, tr);
                    if (names.Count > 0 && !Wild.Any(block, names)) continue;

                    Scale3d s = br.ScaleFactors;
                    var rec = new Dictionary<string, object>
                    {
                        ["handle"] = br.Handle.ToString(),
                        ["block"] = block,
                        ["layer"] = br.Layer,
                        ["layout"] = spaceName,
                        ["x"] = Geo.R(br.Position.X),
                        ["y"] = Geo.R(br.Position.Y),
                        ["rotation"] = Geo.Deg(br.Rotation),
                        ["scale_x"] = Geo.R(s.X),
                        ["scale_y"] = Geo.R(s.Y),
                    };
                    var attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in Describe.Attributes(br, tr))
                    {
                        if (tags.Count > 0 && !tags.Any(t => t.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
                        attributes[kv.Key] = kv.Value;
                        if (!tagColumns.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) tagColumns.Add(kv.Key);
                    }
                    rec["attributes"] = attributes;
                    if (dynamic && br.IsDynamicBlock)
                    {
                        Dictionary<string, object> props = Describe.DynamicProperties(br);
                        rec["dynamic"] = props;
                        foreach (string p in props.Keys)
                            if (!dynColumns.Contains(p, StringComparer.OrdinalIgnoreCase)) dynColumns.Add(p);
                    }
                    records.Add(rec);
                }
            }

            var result = new Dictionary<string, object>
            {
                ["count"] = records.Count,
                ["blocks"] = records.GroupBy(r => (string)r["block"]).ToDictionary(g => g.Key, g => (object)g.Count()),
                ["tags"] = tagColumns,
                ["returned"] = Math.Min(limit, records.Count),
                ["rows"] = records.Take(limit).Cast<object>().ToList(),
            };
            if (dynColumns.Count > 0) result["dynamic_properties"] = dynColumns;
            if (a.Has("csv_path"))
            {
                string[] fixedColumns = { "handle", "block", "layer", "layout", "x", "y", "rotation", "scale_x", "scale_y" };
                var header = fixedColumns.Concat(tagColumns).Concat(dynColumns.Select(d => "dyn:" + d)).ToList();
                var rows = records.Select(r =>
                {
                    var row = fixedColumns.Select(k => r[k]).ToList();
                    var attrs = (Dictionary<string, object>)r["attributes"];
                    row.AddRange(tagColumns.Select(t => attrs.TryGetValue(t, out object v) ? v : null));
                    var dyn = r.TryGetValue("dynamic", out object dv) ? (Dictionary<string, object>)dv : null;
                    row.AddRange(dynColumns.Select(p => dyn != null && dyn.TryGetValue(p, out object v) ? v : null));
                    return (IList<object>)row;
                });
                result["csv"] = Csv.Write(a.ReqStr("csv_path"), header, rows, a.Str("delimiter"));
            }
            return result;
        }
    }
}
