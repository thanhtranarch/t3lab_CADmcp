using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    internal static class QueryTools
    {
        public static void Register()
        {
            Registry.Add("query_entities", "Find entities", ToolKind.Read,
                "Find entities in a space (or by handle) with filters, and get their geometry. Returns total_matched, " +
                "counts_by_type / counts_by_layer and one page of entities (limit/offset). Types are DXF names: LINE, LWPOLYLINE, " +
                "CIRCLE, ARC, TEXT, MTEXT, INSERT (block reference), DIMENSION, HATCH, POINT, SPLINE, ELLIPSE, MULTILEADER, VIEWPORT... " +
                "Use detail=count to only count, full for color/linetype/bbox/all vertices.",
                Obj(Space,
                    P("types", Strs("DXF type names, wildcards allowed")),
                    P("layers", Strs("Layer names, wildcards allowed")),
                    P("block_names", Strs("For INSERT: block names (dynamic blocks by their visible name), wildcards allowed")),
                    P("text_contains", Str("Keep entities whose text, attribute values or block name contain this (case-insensitive)")),
                    P("window", Pts("[[x1, y1], [x2, y2]] rectangle in WCS")),
                    P("window_mode", Enum("intersect (default): bounding box overlaps the window; inside: fully inside", "intersect", "inside")),
                    Handles,
                    P("detail", Enum("summary (default), full or count", "summary", "full", "count")),
                    P("limit", Int("Max entities returned (default 100, max 2000)")),
                    P("offset", Int("Skip this many matches (paging)"))),
                Query);

            Registry.Add("get_texts", "Read texts", ToolKind.Read,
                "All readable text in a space: TEXT, MTEXT (plain), block attributes (ATTRIB, with block name and tag) and multileader text, " +
                "with position and height. Good for reading a drawing: room names, labels, title block fields.",
                Obj(Space,
                    P("contains", Str("Keep texts containing this (case-insensitive)")),
                    P("layers", Strs("Layer names, wildcards allowed")),
                    P("include_attributes", Bool("Include block attribute values (default true)")),
                    P("sort", Enum("drawing (default) or reading: top-to-bottom, left-to-right", "drawing", "reading")),
                    P("limit", Int("Max texts returned (default 500)")),
                    P("offset", Int("Skip this many (paging)"))),
                GetTexts);

            Registry.Add("list_blocks", "List block definitions", ToolKind.Read,
                "Block definitions of the active drawing with attribute definitions, entity count, insert count, dynamic/xref flags.",
                Obj(P("name", Str("Wildcard filter")),
                    P("include_anonymous", Bool("Include anonymous *U / *D blocks (default false)")),
                    P("include_xrefs", Bool("Include xrefs (default true)"))),
                ListBlocks);

            Registry.Add("selection", "Get / set selection", ToolKind.Write,
                "get: the entities the user has selected (pickfirst). set / add: select these handles in AutoCAD (grips) so the user sees them. clear: deselect.",
                ObjR("action", P("action", Enum("get, set, add or clear", "get", "set", "add", "clear")), Handles,
                    P("limit", Int("Max entities described for get (default 200)"))),
                Selection);
        }

        static object Query(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string detail = (a.Str("detail") ?? "summary").ToLowerInvariant();
            bool full = detail == "full", countOnly = detail == "count";
            int limit = Math.Max(1, Math.Min(2000, a.Int("limit", 100)));
            int offset = Math.Max(0, a.Int("offset", 0));
            List<string> types = a.Strs("types").Select(t => t.ToUpperInvariant()).ToList();
            List<string> layers = a.Strs("layers");
            List<string> blocks = a.Strs("block_names");
            string contains = a.Str("text_contains");
            Extents3d? window = a.Has("window") ? Window(a.Raw("window")) : (Extents3d?)null;
            bool inside = string.Equals(a.Str("window_mode"), "inside", StringComparison.OrdinalIgnoreCase);

            IEnumerable<ObjectId> source;
            string spaceName;
            var missing = new List<string>();
            if (a.Has("handles"))
            {
                var ids = new List<ObjectId>();
                foreach (string h in a.Strs("handles"))
                {
                    if (Acad.TryId(db, h, out ObjectId id)) ids.Add(id);
                    else missing.Add(h);
                }
                source = ids;
                spaceName = "(by handle)";
            }
            else
            {
                ObjectId spaceId = Acad.SpaceId(db, tr, a.Str("space"));
                spaceName = Acad.SpaceName(tr, spaceId);
                source = ((BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead)).Cast<ObjectId>();
            }

            var byType = new SortedDictionary<string, int>();
            var byLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var items = new List<object>();
            int matched = 0;
            foreach (ObjectId id in source)
            {
                if (id.IsErased) continue;
                string dxf = id.ObjectClass.DxfName;
                if (types.Count > 0 && !Wild.Any(dxf, types)) continue;
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                if (!Wild.Any(e.Layer, layers)) continue;
                if (blocks.Count > 0 && !(e is BlockReference br && Wild.Any(Acad.EffectiveName(br, tr), blocks))) continue;
                if (contains != null && Acad.TextOf(e, tr).IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (window.HasValue && !Hit(e, window.Value, inside)) continue;

                matched++;
                byType[dxf] = byType.TryGetValue(dxf, out int nt) ? nt + 1 : 1;
                byLayer[e.Layer] = byLayer.TryGetValue(e.Layer, out int nl) ? nl + 1 : 1;
                if (!countOnly && matched > offset && items.Count < limit) items.Add(Describe.Entity(e, tr, full));
            }

            var result = new Dictionary<string, object>
            {
                ["space"] = spaceName,
                ["total_matched"] = matched,
                ["counts_by_type"] = byType.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                ["counts_by_layer"] = byLayer.OrderByDescending(kv => kv.Value).Take(60).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            };
            if (byLayer.Count > 60) result["layers_not_listed"] = byLayer.Count - 60;
            if (!countOnly)
            {
                result["offset"] = offset;
                result["returned"] = items.Count;
                result["has_more"] = matched > offset + items.Count;
                result["entities"] = items;
            }
            if (missing.Count > 0) result["missing_handles"] = missing;
            return result;
        }

        static Extents3d Window(object raw)
        {
            if (!(raw is IList corners) || corners.Count != 2) throw new ToolError("window must be [[x1, y1], [x2, y2]].");
            Point3d p1 = Args.ToPoint(corners[0], "window[0]"), p2 = Args.ToPoint(corners[1], "window[1]");
            return new Extents3d(
                new Point3d(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), -1e99),
                new Point3d(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), 1e99));
        }

        static bool Hit(Entity e, Extents3d w, bool inside)
        {
            Extents3d? b = e.Bounds;
            if (!b.HasValue) return false;
            Point3d min = b.Value.MinPoint, max = b.Value.MaxPoint;
            if (inside)
                return min.X >= w.MinPoint.X && min.Y >= w.MinPoint.Y && max.X <= w.MaxPoint.X && max.Y <= w.MaxPoint.Y;
            return min.X <= w.MaxPoint.X && max.X >= w.MinPoint.X && min.Y <= w.MaxPoint.Y && max.Y >= w.MinPoint.Y;
        }

        static object GetTexts(ToolContext c, Args a)
        {
            var tr = c.Tr;
            ObjectId spaceId = Acad.SpaceId(c.Db, tr, a.Str("space"));
            string contains = a.Str("contains");
            List<string> layers = a.Strs("layers");
            bool attributes = a.Bool("include_attributes", true);
            int limit = Math.Max(1, Math.Min(5000, a.Int("limit", 500)));
            int offset = Math.Max(0, a.Int("offset", 0));

            var found = new List<Dictionary<string, object>>();
            var positions = new List<Point3d>();
            void Add(Dictionary<string, object> item, Point3d at)
            {
                string text = (string)item["text"];
                if (string.IsNullOrWhiteSpace(text)) return;
                if (contains != null && text.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) return;
                if (!Wild.Any((string)item["layer"], layers)) return;
                found.Add(item);
                positions.Add(at);
            }

            foreach (ObjectId id in (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead))
            {
                string dxf = id.ObjectClass.DxfName;
                if (dxf != "TEXT" && dxf != "MTEXT" && dxf != "MULTILEADER" && !(attributes && dxf == "INSERT")) continue;
                var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                switch (e)
                {
                    case DBText t:
                        Add(TextItem(t.Handle, "TEXT", t.Layer, t.TextString, Acad.TextPoint(t), t.Height), Acad.TextPoint(t));
                        break;
                    case MText m:
                        Add(TextItem(m.Handle, "MTEXT", m.Layer, m.Text, m.Location, m.TextHeight), m.Location);
                        break;
                    case MLeader ml when ml.ContentType == ContentType.MTextContent && ml.MText != null:
                        Add(TextItem(ml.Handle, "MULTILEADER", ml.Layer, ml.MText.Text, ml.MText.Location, ml.MText.TextHeight), ml.MText.Location);
                        break;
                    case BlockReference br:
                        string block = null;
                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            if (!(tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar)) continue;
                            block = block ?? Acad.EffectiveName(br, tr);
                            Point3d at = Acad.TextPoint(ar);
                            var item = TextItem(ar.Handle, "ATTRIB", ar.Layer, ar.TextString, at, ar.Height);
                            item["tag"] = ar.Tag;
                            item["block"] = block;
                            item["owner"] = br.Handle.ToString();
                            if (ar.Invisible) item["invisible"] = true;
                            Add(item, at);
                        }
                        break;
                }
            }

            IEnumerable<int> order = Enumerable.Range(0, found.Count);
            if (string.Equals(a.Str("sort"), "reading", StringComparison.OrdinalIgnoreCase))
                order = order.OrderByDescending(i => Math.Round(positions[i].Y, 3)).ThenBy(i => positions[i].X);
            List<object> page = order.Skip(offset).Take(limit).Select(i => (object)found[i]).ToList();
            return new Dictionary<string, object>
            {
                ["space"] = Acad.SpaceName(tr, spaceId),
                ["total"] = found.Count,
                ["offset"] = offset,
                ["returned"] = page.Count,
                ["has_more"] = found.Count > offset + page.Count,
                ["texts"] = page,
            };
        }

        static Dictionary<string, object> TextItem(Handle h, string type, string layer, string text, Point3d at, double height) =>
            new Dictionary<string, object>
            {
                ["handle"] = h.ToString(),
                ["type"] = type,
                ["layer"] = layer,
                ["text"] = text ?? "",
                ["position"] = Geo.P(at),
                ["height"] = Geo.R(height),
            };

        static object ListBlocks(ToolContext c, Args a)
        {
            var tr = c.Tr;
            string filter = a.Str("name");
            bool anonymous = a.Bool("include_anonymous", false), xrefs = a.Bool("include_xrefs", true);
            var table = (BlockTable)tr.GetObject(c.Db.BlockTableId, OpenMode.ForRead);
            var blocks = new List<object>();
            foreach (ObjectId id in table)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsLayout || btr.IsErased) continue;
                if (btr.IsAnonymous && !anonymous) continue;
                if (btr.IsFromExternalReference && !xrefs) continue;
                if (btr.IsDependent) continue; // blocks nested inside xrefs
                if (filter != null && !Wild.Match(btr.Name, filter)) continue;

                int entities = 0;
                var attdefs = new List<object>();
                foreach (ObjectId eid in btr)
                {
                    entities++;
                    if (eid.ObjectClass.DxfName == "ATTDEF" && tr.GetObject(eid, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
                        attdefs.Add(new Dictionary<string, object> { ["tag"] = ad.Tag, ["prompt"] = ad.Prompt, ["default"] = ad.TextString });
                }

                int inserts = btr.GetBlockReferenceIds(true, false).Count;
                if (btr.IsDynamicBlock)
                {
                    foreach (ObjectId anonId in btr.GetAnonymousBlockIds())
                        inserts += ((BlockTableRecord)tr.GetObject(anonId, OpenMode.ForRead)).GetBlockReferenceIds(true, false).Count;
                }

                var item = new Dictionary<string, object>
                {
                    ["name"] = btr.Name,
                    ["entities"] = entities,
                    ["inserts"] = inserts,
                    ["origin"] = Geo.P(btr.Origin),
                };
                if (btr.IsDynamicBlock) item["dynamic"] = true;
                if (btr.IsAnonymous) item["anonymous"] = true;
                if (btr.IsFromExternalReference) { item["xref"] = true; item["xref_path"] = btr.PathName; }
                if (attdefs.Count > 0) item["attributes"] = attdefs;
                if (!string.IsNullOrEmpty(btr.Comments)) item["description"] = btr.Comments;
                blocks.Add(item);
            }
            return new Dictionary<string, object> { ["count"] = blocks.Count, ["blocks"] = blocks };
        }

        static object Selection(ToolContext c, Args a)
        {
            string action = (a.Str("action") ?? "get").ToLowerInvariant();
            Editor ed = c.Ed;
            ObjectId[] Current()
            {
                PromptSelectionResult r = ed.SelectImplied();
                return r.Status == PromptStatus.OK && r.Value != null ? r.Value.GetObjectIds() : new ObjectId[0];
            }

            switch (action)
            {
                case "get":
                    ObjectId[] ids = Current();
                    int limit = Math.Max(1, a.Int("limit", 200));
                    return new Dictionary<string, object>
                    {
                        ["count"] = ids.Length,
                        ["entities"] = ids.Take(limit).Select(id => (object)Describe.Entity((Entity)c.Tr.GetObject(id, OpenMode.ForRead), c.Tr, false)).ToList(),
                    };
                case "set":
                case "add":
                    var target = new List<ObjectId>(action == "add" ? Current() : new ObjectId[0]);
                    foreach (ObjectId id in Acad.Ids(c.Db, a.Strs("handles")))
                        if (!target.Contains(id)) target.Add(id);
                    ed.SetImpliedSelection(target.ToArray());
                    return new Dictionary<string, object> { ["selected"] = target.Count };
                case "clear":
                    ed.SetImpliedSelection(new ObjectId[0]);
                    return new Dictionary<string, object> { ["selected"] = 0 };
                default:
                    throw new ToolError("action must be get, set, add or clear.");
            }
        }
    }
}
