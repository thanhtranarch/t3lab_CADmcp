using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>Bulk edits: replace_text, edit_geometry (offset / join / boundary), sync_block_attributes.</summary>
    internal static class ModifyTools
    {
        static readonly string[] TextKinds = { "text", "mtext", "attribute", "dimension", "mleader", "table" };

        public static void Register()
        {
            Registry.Add("replace_text", "Find and replace text", ToolKind.Write,
                "Find and replace in TEXT, MTEXT, block attribute values, dimension text overrides, multileader text and table cells " +
                "(one undo step). Plain text by default, regex=true for regular expressions ($1 in replace). dry_run=true only counts " +
                "and shows samples. Entities on locked layers are skipped and counted; an MTEXT whose formatting splits the match is reported, not changed.",
                ObjR("find,replace",
                    P("find", Str("Text to find (or a regular expression with regex=true)")),
                    P("replace", Str("Replacement (may be empty)")),
                    P("regex", Bool("find is a .NET regular expression (default false)")),
                    P("match_case", Bool("Case sensitive (default false)")),
                    P("whole_word", Bool("Only whole words (default false)")),
                    P("types", EnumArr("Where to look (default all)", TextKinds)),
                    P("layers", Strs("Layer names, wildcards allowed")),
                    P("space", Str("all (default), model, paper, current or a layout name")),
                    P("include_blocks", Bool("Also change text inside block definitions (default false)")),
                    P("dry_run", Bool("Count and show samples only (default false)")),
                    P("limit", Int("Max samples returned (default 50)"))),
                ReplaceText);

            Registry.Add("edit_geometry", "Offset / join / boundary", ToolKind.Write,
                "Geometry edits. offset {handles (curves), distance, side_point (which side) or both=true}; " +
                "join {handles: lines, arcs, polylines touching end to end, joined into one polyline (or splines into a spline)}; " +
                "boundary {point inside a closed area, islands} creates the closed polyline(s) around it, like BOUNDARY: the area must " +
                "be visible on screen (zoom first). New entities are returned with their length / area.",
                ObjR("operation",
                    P("operation", Enum("What to do", "offset", "join", "boundary")),
                    Handles,
                    P("distance", Num("offset: distance (> 0)")),
                    P("side_point", Pt("offset: a point on the side to offset to")),
                    P("both", Bool("offset: both sides (default false)")),
                    P("point", Pt("boundary: a point inside the area")),
                    P("islands", Bool("boundary: detect islands inside the area (default true)")),
                    P("layer", Str("Layer for the new entities (default: as the source / current layer)"))),
                EditGeometry);

            Registry.Add("sync_block_attributes", "Sync block attributes", ToolKind.Write,
                "Like ATTSYNC: make every insert of the blocks match the block definition attributes. Missing attributes are added " +
                "with their default value, existing values are kept, attributes no longer in the definition are removed (remove_extra), " +
                "and position / style / height follow the definition unless keep_positions=true. Inserts on locked layers are skipped.",
                ObjR("names",
                    P("names", Strs("Block names, wildcards allowed")),
                    P("keep_positions", Bool("Keep the position and look of existing attributes (default false)")),
                    P("remove_extra", Bool("Remove attributes whose tag is not in the definition (default true)"))),
                SyncAttributes);
        }

        // ---------------------------------------------------------------- replace_text

        sealed class ReplaceState
        {
            public Regex Rx;
            public string Replacement;
            public bool DryRun;
            public int Limit;
            public readonly Dictionary<string, int> Matches = new Dictionary<string, int>();
            public readonly List<object> Samples = new List<object>();
            public readonly HashSet<ObjectId> Changed = new HashSet<ObjectId>();
            public int LockedSkipped, FormattingSkipped;
            public readonly Dictionary<ObjectId, bool> LockedLayers = new Dictionary<ObjectId, bool>();
        }

        static object ReplaceText(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string find = a.Str("find") ?? "";
            if (find.Length == 0) throw new ToolError("find is empty.");
            string replace = a.Str("replace") ?? "";
            bool regex = a.Bool("regex", false);
            var options = RegexOptions.CultureInvariant | (a.Bool("match_case", false) ? RegexOptions.None : RegexOptions.IgnoreCase);
            string pattern = regex ? find : Regex.Escape(find);
            if (a.Bool("whole_word", false)) pattern = @"\b" + pattern + @"\b";
            var st = new ReplaceState
            {
                Replacement = regex ? replace : replace.Replace("$", "$$"),
                DryRun = a.Bool("dry_run", false),
                Limit = Math.Max(0, a.Int("limit", 50)),
            };
            try { st.Rx = new Regex(pattern, options, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { throw new ToolError("Invalid regular expression: " + ex.Message); }

            var kinds = a.Has("types") ? a.Strs("types").Select(t => t.Trim().ToLowerInvariant()).ToList() : TextKinds.ToList();
            foreach (string k in kinds)
                if (!TextKinds.Contains(k)) throw new ToolError($"Unknown type '{k}'. Use {string.Join(", ", TextKinds)}.");
            List<string> layers = a.Strs("layers");

            var owners = Acad.SpaceIds(db, tr, a.Str("space") ?? "all");
            if (a.Bool("include_blocks", false))
            {
                foreach (ObjectId id in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!btr.IsLayout && !btr.IsFromExternalReference && !btr.IsDependent && !btr.IsAnonymous) owners.Add(id);
                }
            }

            foreach (ObjectId ownerId in owners)
            {
                foreach (ObjectId id in (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead))
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    if (!Wild.Any(e.Layer, layers)) continue;
                    try
                    {
                        switch (e)
                        {
                            case AttributeDefinition ad when kinds.Contains("attribute"):
                                Apply(st, tr, ad, "attribute", ad.TextString, v => ad.TextString = v);
                                break;
                            case DBText t when !(t is AttributeDefinition) && kinds.Contains("text"):
                                Apply(st, tr, t, "text", t.TextString, v => t.TextString = v);
                                break;
                            case MText m when kinds.Contains("mtext"):
                                if (!Apply(st, tr, m, "mtext", m.Contents, v => m.Contents = v) && st.Rx.IsMatch(m.Text ?? ""))
                                    st.FormattingSkipped++;
                                break;
                            case Dimension d when kinds.Contains("dimension") && !string.IsNullOrEmpty(d.DimensionText):
                                Apply(st, tr, d, "dimension", d.DimensionText, v => d.DimensionText = v);
                                break;
                            case MLeader ml when kinds.Contains("mleader") && ml.ContentType == ContentType.MTextContent && ml.MText != null:
                                Apply(st, tr, ml, "mleader", ml.MText.Contents, v =>
                                {
                                    MText copy = ml.MText;
                                    copy.Contents = v;
                                    ml.MText = copy;
                                });
                                break;
                            case Table tb when kinds.Contains("table"):
                                bool any = false;
                                for (int r = 0; r < tb.Rows.Count; r++)
                                    for (int col = 0; col < tb.Columns.Count; col++)
                                    {
                                        int rr = r, cc = col;
                                        if (Apply(st, tr, tb, "table", tb.Cells[rr, cc].TextString ?? "", v => tb.Cells[rr, cc].TextString = v)) any = true;
                                    }
                                if (any && !st.DryRun) tb.GenerateLayout();
                                break;
                            case BlockReference br when kinds.Contains("attribute"):
                                foreach (ObjectId aid in br.AttributeCollection)
                                {
                                    if (!(tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar) || ar.IsErased) continue;
                                    Apply(st, tr, ar, "attribute", ar.TextString, v =>
                                    {
                                        ar.TextString = v;
                                        if (ar.IsMTextAttribute) ar.UpdateMTextAttribute();
                                    });
                                }
                                break;
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        throw new ToolError("The regular expression took too long on a text: simplify it. Nothing was changed.");
                    }
                }
            }

            var result = new Dictionary<string, object>
            {
                ["dry_run"] = st.DryRun,
                ["matches"] = st.Matches.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                ["total_matches"] = st.Matches.Values.Sum(),
                [st.DryRun ? "would_change_entities" : "changed_entities"] = st.Changed.Count,
                ["samples"] = st.Samples,
            };
            if (st.LockedSkipped > 0) result["skipped_on_locked_layers"] = st.LockedSkipped;
            if (st.FormattingSkipped > 0) result["mtext_not_changed_formatting"] = st.FormattingSkipped;
            if (!st.DryRun && st.Changed.Count > 0) c.NeedsRegen = true;
            return result;
        }

        /// <summary>Replace in one text value; returns true when it matched (and was changed, unless dry run or locked).</summary>
        static bool Apply(ReplaceState st, Transaction tr, Entity owner, string kind, string text, Action<string> set)
        {
            text = text ?? "";
            int n = st.Rx.Matches(text).Count;
            if (n == 0) return false;
            string after = st.Rx.Replace(text, st.Replacement);
            if (after == text) return false;
            if (IsLocked(st, tr, owner.LayerId))
            {
                st.LockedSkipped++;
                return false;
            }
            st.Matches[kind] = (st.Matches.TryGetValue(kind, out int m) ? m : 0) + n;
            if (st.Samples.Count < st.Limit)
                st.Samples.Add(new Dictionary<string, object>
                {
                    ["handle"] = owner.Handle.ToString(),
                    ["type"] = kind,
                    ["before"] = Clip(text),
                    ["after"] = Clip(after),
                });
            st.Changed.Add(owner.ObjectId);
            if (!st.DryRun)
            {
                if (!owner.IsWriteEnabled) owner.UpgradeOpen();
                set(after);
            }
            return true;
        }

        static string Clip(string s) => s.Length <= 200 ? s : s.Substring(0, 200) + "...";

        static bool IsLocked(ReplaceState st, Transaction tr, ObjectId layerId)
        {
            if (!st.LockedLayers.TryGetValue(layerId, out bool locked))
                st.LockedLayers[layerId] = locked = ((LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead)).IsLocked;
            return locked;
        }

        // ---------------------------------------------------------------- edit_geometry

        static object EditGeometry(ToolContext c, Args a)
        {
            string op = a.ReqStr("operation").Trim().ToLowerInvariant();
            ObjectId layerId = a.Has("layer") ? Acad.EnsureLayer(c.Db, c.Tr, a.ReqStr("layer")) : ObjectId.Null;
            switch (op)
            {
                case "offset": return Offset(c, a, layerId);
                case "join": return Join(c, a, layerId);
                case "boundary": return Boundary(c, a, layerId);
                default: throw new ToolError("operation must be offset, join or boundary.");
            }
        }

        static Dictionary<string, object> NewInfo(Entity e)
        {
            var d = new Dictionary<string, object> { ["handle"] = e.Handle.ToString(), ["type"] = e.GetRXClass().DxfName };
            double? length = Metrics.Length(e), area = Metrics.Area(e);
            if (length.HasValue) d["length"] = Geo.R(length.Value);
            if (area.HasValue) d["area"] = Geo.R(area.Value);
            return d;
        }

        static void AppendNew(ToolContext c, BlockTableRecord space, Entity e, ObjectId layerId)
        {
            if (!layerId.IsNull) e.LayerId = layerId;
            Acad.Append(space, e, c.Tr);
        }

        static void DisposeAll(IEnumerable<DBObject> objects)
        {
            foreach (DBObject o in objects)
                if (o != null && o.ObjectId.IsNull && !o.IsDisposed) o.Dispose();
        }

        static object Offset(ToolContext c, Args a, ObjectId layerId)
        {
            double distance = a.ReqNum("distance");
            if (distance <= 0) throw new ToolError("distance must be greater than 0 (side_point picks the side).");
            bool both = a.Bool("both", false);
            Point3d? side = a.Has("side_point") ? a.ReqPt("side_point") : (Point3d?)null;
            if (!both && side == null) throw new ToolError("Pass side_point (a point on the side to offset to) or both=true.");

            var created = new List<object>();
            var errors = new List<object>();
            foreach (ObjectId id in Acad.Ids(c.Db, a.Strs("handles")))
            {
                var made = new List<DBObject>();
                try
                {
                    if (!(c.Tr.GetObject(id, OpenMode.ForRead) is Curve curve)) throw new ToolError("not a curve");
                    var plus = curve.GetOffsetCurves(distance).Cast<DBObject>().ToList();
                    var minus = curve.GetOffsetCurves(-distance).Cast<DBObject>().ToList();
                    made.AddRange(plus);
                    made.AddRange(minus);
                    List<DBObject> keep = both ? made : Nearer(plus, minus, side.Value);
                    if (keep.Count == 0) throw new ToolError("this curve cannot be offset by that distance");
                    var space = (BlockTableRecord)c.Tr.GetObject(curve.OwnerId, OpenMode.ForWrite);
                    foreach (Entity e in keep.OfType<Entity>())
                    {
                        AppendNew(c, space, e, layerId);
                        created.Add(NewInfo(e));
                    }
                }
                catch (ToolError te) { errors.Add(new Dictionary<string, object> { ["handle"] = id.Handle.ToString(), ["error"] = te.Message }); }
                catch (AcRx.Exception ae) { errors.Add(new Dictionary<string, object> { ["handle"] = id.Handle.ToString(), ["error"] = "AutoCAD error " + ae.ErrorStatus }); }
                finally { DisposeAll(made); }
            }
            if (created.Count == 0) throw new ToolError("Nothing was offset: " + Json.Write(errors));
            var result = new Dictionary<string, object> { ["operation"] = "offset", ["created"] = created };
            if (errors.Count > 0) result["errors"] = errors;
            return result;
        }

        /// <summary>The offset result whose curves pass closer to the side point.</summary>
        static List<DBObject> Nearer(List<DBObject> plus, List<DBObject> minus, Point3d side)
        {
            double Dist(List<DBObject> set) => set.OfType<Curve>().Select(cv =>
            {
                try { return cv.GetClosestPointTo(side, false).DistanceTo(side); }
                catch (AcRx.Exception) { return double.MaxValue; }
            }).DefaultIfEmpty(double.MaxValue).Min();
            return Dist(plus) <= Dist(minus) ? plus : minus;
        }

        static object Join(ToolContext c, Args a, ObjectId layerId)
        {
            var tr = c.Tr;
            List<ObjectId> ids = Acad.Ids(c.Db, a.Strs("handles"));
            if (ids.Count < 2) throw new ToolError("join needs at least 2 handles.");
            var first = (Entity)tr.GetObject(ids[0], OpenMode.ForWrite);
            var space = (BlockTableRecord)tr.GetObject(first.OwnerId, OpenMode.ForWrite);

            // lines and arcs join through a polyline, as JOIN does
            Entity primary = first;
            Polyline converted = ToPolyline(first);
            if (converted != null)
            {
                converted.SetPropertiesFrom(first);
                Acad.Append(space, converted, tr);
                first.Erase();
                primary = converted;
            }

            var pending = ids.Skip(1).Select(id => (Entity)tr.GetObject(id, OpenMode.ForWrite)).ToList();
            var joined = new List<Entity>();
            // several passes: a piece can only join once a neighbour has extended the end it touches
            for (int pass = 0; pass < pending.Count + 1 && pending.Count > 0; pass++)
            {
                IntegerCollection done;
                try { done = primary.JoinEntities(pending.ToArray()); }
                catch (AcRx.Exception) { break; }
                if (done == null || done.Count == 0) break;
                var hit = new HashSet<int>(done.Cast<int>());
                joined.AddRange(pending.Where((e, i) => hit.Contains(i)));
                pending = pending.Where((e, i) => !hit.Contains(i)).ToList();
            }
            if (joined.Count == 0)
                throw new ToolError("Nothing could be joined: the pieces must touch end to end (and lie in one plane). Nothing was changed.");
            foreach (Entity e in joined) e.Erase();

            if (primary is Polyline pl && !pl.Closed && pl.NumberOfVertices > 2 &&
                pl.GetPoint2dAt(0).GetDistanceTo(pl.GetPoint2dAt(pl.NumberOfVertices - 1)) < 1e-8)
            {
                pl.RemoveVertexAt(pl.NumberOfVertices - 1);
                pl.Closed = true;
            }
            if (!layerId.IsNull) primary.LayerId = layerId;
            var result = new Dictionary<string, object>
            {
                ["operation"] = "join",
                ["result"] = NewInfo(primary),
                ["joined"] = joined.Count + 1,
            };
            if (primary is Curve pc) result["closed"] = pc.Closed;
            if (pending.Count > 0) result["not_joined"] = pending.Select(e => (object)e.Handle.ToString()).ToList();
            return result;
        }

        /// <summary>A polyline copy of a line or a flat arc; null for anything else.</summary>
        static Polyline ToPolyline(Entity e)
        {
            switch (e)
            {
                case Line l when Math.Abs(l.StartPoint.Z) < 1e-9 && Math.Abs(l.EndPoint.Z) < 1e-9:
                {
                    var pl = new Polyline();
                    pl.AddVertexAt(0, new Point2d(l.StartPoint.X, l.StartPoint.Y), 0, 0, 0);
                    pl.AddVertexAt(1, new Point2d(l.EndPoint.X, l.EndPoint.Y), 0, 0, 0);
                    return pl;
                }
                case Arc ar when ar.Normal.IsParallelTo(Vector3d.ZAxis) && ar.Normal.Z > 0 && Math.Abs(ar.Center.Z) < 1e-9:
                {
                    double sweep = ar.EndAngle - ar.StartAngle;
                    if (sweep < 0) sweep += 2 * Math.PI;
                    var pl = new Polyline();
                    pl.AddVertexAt(0, new Point2d(ar.StartPoint.X, ar.StartPoint.Y), Math.Tan(sweep / 4), 0, 0);
                    pl.AddVertexAt(1, new Point2d(ar.EndPoint.X, ar.EndPoint.Y), 0, 0, 0);
                    return pl;
                }
                default:
                    return null;
            }
        }

        static object Boundary(ToolContext c, Args a, ObjectId layerId)
        {
            Point3d seed = a.ReqPt("point");
            DBObjectCollection loops = c.Ed.TraceBoundary(seed, a.Bool("islands", true));
            var objects = loops.Cast<DBObject>().ToList();
            try
            {
                if (objects.Count == 0)
                    throw new ToolError("No closed boundary was found around that point. Zoom so the whole area is visible on screen " +
                                        "(boundary only sees what is displayed) and check that the outline is closed.");
                var space = (BlockTableRecord)c.Tr.GetObject(c.Db.CurrentSpaceId, OpenMode.ForWrite);
                var created = new List<object>();
                foreach (Entity e in objects.OfType<Entity>())
                {
                    e.SetDatabaseDefaults(c.Db);
                    AppendNew(c, space, e, layerId);
                    created.Add(NewInfo(e));
                }
                return new Dictionary<string, object> { ["operation"] = "boundary", ["created"] = created };
            }
            finally
            {
                DisposeAll(objects);
            }
        }

        // ---------------------------------------------------------------- sync_block_attributes

        static object SyncAttributes(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            List<string> names = a.Strs("names");
            if (names.Count == 0) throw new ToolError("'names' is empty.");
            bool keepPositions = a.Bool("keep_positions", false);
            bool removeExtra = a.Bool("remove_extra", true);
            var lockedCache = new ReplaceState();

            var report = new List<object>();
            foreach (ObjectId btrId in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference || btr.IsDependent) continue;
                if (!Wild.Any(btr.Name, names)) continue;

                var refs = btr.GetBlockReferenceIds(true, false).Cast<ObjectId>().ToList();
                if (btr.IsDynamicBlock)
                    foreach (ObjectId anon in btr.GetAnonymousBlockIds())
                        refs.AddRange(((BlockTableRecord)tr.GetObject(anon, OpenMode.ForRead)).GetBlockReferenceIds(true, false).Cast<ObjectId>());

                int inserts = 0, added = 0, removed = 0, updated = 0, locked = 0;
                foreach (ObjectId refId in refs.Distinct())
                {
                    if (refId.IsErased || !(tr.GetObject(refId, OpenMode.ForRead) is BlockReference br)) continue;
                    if (IsLocked(lockedCache, tr, br.LayerId)) { locked++; continue; }
                    inserts++;
                    var definition = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                    var defs = definition.Cast<ObjectId>()
                        .Where(id => id.ObjectClass.DxfName == "ATTDEF")
                        .Select(id => (AttributeDefinition)tr.GetObject(id, OpenMode.ForRead))
                        .Where(d => !d.Constant).ToList();
                    var existing = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId aid in br.AttributeCollection)
                        if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar && !ar.IsErased && !existing.ContainsKey(ar.Tag))
                            existing[ar.Tag] = ar;

                    foreach (AttributeDefinition def in defs)
                    {
                        if (existing.TryGetValue(def.Tag, out AttributeReference ar))
                        {
                            if (keepPositions) continue;
                            string value = ar.TextString;
                            ar.UpgradeOpen();
                            ar.SetAttributeFromBlock(def, br.BlockTransform);
                            ar.TextString = value;
                            if (ar.IsMTextAttribute) ar.UpdateMTextAttribute();
                            updated++;
                        }
                        else
                        {
                            if (!br.IsWriteEnabled) br.UpgradeOpen();
                            var fresh = new AttributeReference();
                            fresh.SetAttributeFromBlock(def, br.BlockTransform);
                            fresh.TextString = def.TextString;
                            if (fresh.IsMTextAttribute) fresh.UpdateMTextAttribute();
                            br.AttributeCollection.AppendAttribute(fresh);
                            tr.AddNewlyCreatedDBObject(fresh, true);
                            added++;
                        }
                    }
                    if (removeExtra)
                    {
                        foreach (var kv in existing)
                        {
                            if (defs.Any(d => d.Tag.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
                            kv.Value.UpgradeOpen();
                            kv.Value.Erase();
                            removed++;
                        }
                    }
                }
                var item = new Dictionary<string, object>
                {
                    ["block"] = btr.Name,
                    ["inserts"] = inserts,
                    ["attributes_added"] = added,
                    ["attributes_removed"] = removed,
                    ["attributes_updated"] = updated,
                };
                if (locked > 0) item["skipped_on_locked_layers"] = locked;
                report.Add(item);
            }
            if (report.Count == 0) throw new ToolError("No block definition matches (see list_blocks).");
            c.NeedsRegen = true;
            return new Dictionary<string, object> { ["blocks"] = report };
        }
    }
}
