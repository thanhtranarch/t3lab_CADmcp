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

            Registry.Add("edit_geometry", "Edit geometry", ToolKind.Write,
                "Geometry edits in the XY plane (WCS). offset {handles (curves), distance, side_point (which side) or both=true}; " +
                "join {handles: lines, arcs, polylines touching end to end, joined into one polyline (or splines into a spline)}; " +
                "boundary {point inside a closed area, islands} creates the closed polyline(s) around it, like BOUNDARY: the area must " +
                "be visible on screen (zoom first); extend {handles, boundary_handles (edges), end start / end / both / auto (pick_point " +
                "picks the end)}; trim {handles, boundary_handles (cutting edges), pick_point on the part to remove, or pick_points one " +
                "per handle}; break {handles [one curve], point, point2 (the part between is removed; without point2 the curve is split)}; " +
                "fillet {handles: 2 lines + radius (0 = sharp corner), or 1 polyline = every corner}; chamfer {same, distance, distance2}. " +
                "Lines that cross keep their part on the far side of the corner. New entities come back with their length / area.",
                ObjR("operation",
                    P("operation", Enum("What to do", "offset", "join", "boundary", "extend", "trim", "break", "fillet", "chamfer")),
                    Handles,
                    P("distance", Num("offset: distance (> 0); chamfer: distance on the first line")),
                    P("distance2", Num("chamfer: distance on the second line (default distance)")),
                    P("radius", Num("fillet: radius (default 0)")),
                    P("side_point", Pt("offset: a point on the side to offset to")),
                    P("both", Bool("offset: both sides (default false)")),
                    P("point", Pt("boundary: a point inside the area; break: first point")),
                    P("point2", Pt("break: second point")),
                    P("islands", Bool("boundary: detect islands inside the area (default true)")),
                    P("boundary_handles", Strs("extend / trim: the boundary or cutting edges")),
                    P("pick_point", Pt("trim: a point on the part to remove; extend: near the end to extend")),
                    P("pick_points", Pts("trim: one point per handle")),
                    P("end", Enum("extend: which end (default auto)", "start", "end", "both", "auto")),
                    P("layer", Str("Layer for the new entities of offset / join / boundary (default: as the source / current layer)"))),
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
            public readonly HashSet<ObjectId> Locked = new HashSet<ObjectId>();
            public readonly HashSet<ObjectId> Formatting = new HashSet<ObjectId>();
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
                    if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    if (!Wild.Any(e.Layer, layers)) continue;
                    try
                    {
                        switch (e)
                        {
                            case AttributeDefinition ad when kinds.Contains("attribute"):
                                Apply(st, tr, ad, "attribute", ad.TextString, ad.IsMTextAttributeDefinition, v => ad.TextString = v);
                                break;
                            case DBText t when !(t is AttributeDefinition) && kinds.Contains("text"):
                                Apply(st, tr, t, "text", t.TextString, false, v => t.TextString = v);
                                break;
                            case MText m when kinds.Contains("mtext"):
                                Apply(st, tr, m, "mtext", m.Contents, true, v => m.Contents = v);
                                break;
                            case Dimension d when kinds.Contains("dimension") && !string.IsNullOrEmpty(d.DimensionText):
                                Apply(st, tr, d, "dimension", d.DimensionText, true, v => d.DimensionText = v);
                                break;
                            case MLeader ml when kinds.Contains("mleader") && ml.ContentType == ContentType.MTextContent:
                                // MLeader.MText hands out a copy on every read: read it once, give it back, dispose it
                                using (MText copy = ml.MText)
                                {
                                    if (copy == null) break;
                                    Apply(st, tr, ml, "mleader", copy.Contents, true, v =>
                                    {
                                        copy.Contents = v;
                                        ml.MText = copy;
                                    });
                                }
                                break;
                            case Table tb when kinds.Contains("table"):
                                bool any = false;
                                for (int r = 0; r < tb.Rows.Count; r++)
                                    for (int col = 0; col < tb.Columns.Count; col++)
                                    {
                                        int rr = r, cc = col;
                                        if (Apply(st, tr, tb, "table", tb.Cells[rr, cc].TextString ?? "", true, v => tb.Cells[rr, cc].TextString = v)) any = true;
                                    }
                                if (any && !st.DryRun) tb.GenerateLayout();
                                break;
                            case BlockReference br when kinds.Contains("attribute"):
                                foreach (ObjectId aid in br.AttributeCollection)
                                {
                                    if (aid.IsErased || !(tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar)) continue;
                                    Apply(st, tr, ar, "attribute", ar.TextString, ar.IsMTextAttribute, v =>
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
            if (st.Locked.Count > 0) result["skipped_on_locked_layers"] = st.Locked.Count;
            if (st.Formatting.Count > 0) result["matches_split_by_formatting"] = st.Formatting.Count;
            if (!st.DryRun && st.Changed.Count > 0) c.NeedsRegen = true;
            return result;
        }

        /// <summary>
        /// Replace in one text value. formatted: the value is MTEXT contents (codes such as \P, {\fArial;...}, \H2.5x;):
        /// the search runs on the visible text and only matches made of plain characters are replaced.
        /// Returns true when something was (or, on a dry run, would be) changed.
        /// </summary>
        static bool Apply(ReplaceState st, Transaction tr, Entity owner, string kind, string text, bool formatted, Action<string> set)
        {
            text = text ?? "";
            int replaced;
            bool split;
            string after = formatted ? ReplaceFormatted(st, text, out replaced, out split) : ReplacePlain(st, text, out replaced, out split);
            if (split) st.Formatting.Add(owner.ObjectId);
            if (replaced == 0 || after == text) return false;
            if (IsLocked(st, tr, owner.LayerId))
            {
                st.Locked.Add(owner.ObjectId);
                return false;
            }
            st.Matches[kind] = (st.Matches.TryGetValue(kind, out int m) ? m : 0) + replaced;
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

        static string ReplacePlain(ReplaceState st, string text, out int replaced, out bool split)
        {
            split = false;
            int n = 0;
            string after = st.Rx.Replace(text, match =>
            {
                if (match.Length == 0) return match.Value;
                n++;
                return match.Result(st.Replacement);
            });
            replaced = n;
            return after;
        }

        /// <summary>Visible text of MTEXT contents; for each visible character its range in the contents.</summary>
        sealed class MTextMap
        {
            public readonly System.Text.StringBuilder Visible = new System.Text.StringBuilder();
            public readonly List<int> Start = new List<int>(), Length = new List<int>();
            public readonly List<bool> Replaceable = new List<bool>();

            public void Add(char visible, int start, int length, bool replaceable)
            {
                Visible.Append(visible);
                Start.Add(start);
                Length.Add(length);
                Replaceable.Add(replaceable);
            }
        }

        static bool IsHex(string s, int from, int count)
        {
            if (from + count > s.Length) return false;
            for (int i = from; i < from + count; i++)
                if (!Uri.IsHexDigit(s[i])) return false;
            return true;
        }

        static MTextMap MapMText(string raw)
        {
            var map = new MTextMap();
            int i = 0;
            while (i < raw.Length)
            {
                char ch = raw[i];
                if (ch == '{' || ch == '}') { i++; continue; } // grouping
                if (ch == '\\' && i + 1 < raw.Length)
                {
                    char k = raw[i + 1];
                    switch (k)
                    {
                        case '\\':
                        case '{':
                        case '}':
                            map.Add(k, i, 2, true); // escaped literal
                            i += 2;
                            continue;
                        case 'P':
                        case 'N':
                        case 'X':
                            map.Add('\n', i, 2, false); // paragraph / column break: a word boundary, never replaced
                            i += 2;
                            continue;
                        case '~':
                            map.Add(' ', i, 2, true); // non-breaking space
                            i += 2;
                            continue;
                        case 'L':
                        case 'l':
                        case 'O':
                        case 'o':
                        case 'K':
                        case 'k':
                            i += 2; // underline / overline / strike on-off
                            continue;
                        case 'U':
                            if (i + 2 < raw.Length && raw[i + 2] == '+' && IsHex(raw, i + 3, 4))
                            {
                                map.Add((char)Convert.ToInt32(raw.Substring(i + 3, 4), 16), i, 7, true);
                                i += 7;
                                continue;
                            }
                            break;
                        case 'M':
                            if (i + 3 < raw.Length && raw[i + 2] == '+' && IsHex(raw, i + 4, 4))
                            {
                                i += 8; // \M+nXXXX double-byte character: left alone
                                continue;
                            }
                            break;
                        default:
                            if ("fFHWQTACcpS".IndexOf(k) >= 0)
                            {
                                int end = raw.IndexOf(';', i + 2);
                                i = end < 0 ? raw.Length : end + 1; // \f...; \H...; \S...; and the like
                                continue;
                            }
                            break;
                    }
                }
                map.Add(ch, i, 1, true);
                i++;
            }
            return map;
        }

        static string EscapeMText(string s) =>
            s.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("\r\n", "\\P").Replace("\n", "\\P");

        static string ReplaceFormatted(ReplaceState st, string raw, out int replaced, out bool split)
        {
            replaced = 0;
            split = false;
            MTextMap map = MapMText(raw);
            string visible = map.Visible.ToString();
            var edits = new List<Tuple<int, int, string>>();
            foreach (Match match in st.Rx.Matches(visible))
            {
                if (match.Length == 0) continue;
                int first = match.Index, last = match.Index + match.Length - 1;
                bool plain = true;
                for (int i = first; i <= last && plain; i++)
                {
                    if (!map.Replaceable[i]) plain = false;
                    else if (i < last && map.Start[i] + map.Length[i] != map.Start[i + 1]) plain = false; // a code sits inside the match
                }
                if (!plain) { split = true; continue; }
                edits.Add(Tuple.Create(map.Start[first], map.Start[last] + map.Length[last], EscapeMText(match.Result(st.Replacement))));
            }
            if (edits.Count == 0) return raw;
            var sb = new System.Text.StringBuilder(raw);
            for (int i = edits.Count - 1; i >= 0; i--)
            {
                sb.Remove(edits[i].Item1, edits[i].Item2 - edits[i].Item1);
                sb.Insert(edits[i].Item1, edits[i].Item3);
            }
            replaced = edits.Count;
            return sb.ToString();
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
                case "extend": return GeometryOps.Extend(c, a);
                case "trim": return GeometryOps.Trim(c, a);
                case "break": return GeometryOps.Break(c, a);
                case "fillet": return GeometryOps.Corner(c, a, true);
                case "chamfer": return GeometryOps.Corner(c, a, false);
                default: throw new ToolError("operation must be offset, join, boundary, extend, trim, break, fillet or chamfer.");
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
                    // each side on its own: a circle offset inward by more than its radius fails, the outward side still works
                    List<DBObject> plus = OffsetSide(curve, distance, made), minus = OffsetSide(curve, -distance, made);
                    List<DBObject> keep = both ? plus.Concat(minus).ToList() : Nearer(plus, minus, side.Value);
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

        /// <summary>Offset curves on one side; empty when AutoCAD cannot offset that way. Every result is tracked in made (disposed later unless added).</summary>
        static List<DBObject> OffsetSide(Curve curve, double distance, List<DBObject> made)
        {
            try
            {
                var result = curve.GetOffsetCurves(distance).Cast<DBObject>().ToList();
                made.AddRange(result);
                return result;
            }
            catch (AcRx.Exception)
            {
                return new List<DBObject>();
            }
        }

        /// <summary>The offset result whose curves pass closer to the side point (an empty side never wins).</summary>
        static List<DBObject> Nearer(List<DBObject> plus, List<DBObject> minus, Point3d side)
        {
            double Dist(List<DBObject> set) => set.OfType<Curve>().Select(cv =>
            {
                try { return cv.GetClosestPointTo(side, false).DistanceTo(side); }
                catch (AcRx.Exception) { return double.MaxValue; }
            }).DefaultIfEmpty(double.MaxValue).Min();
            if (plus.Count == 0) return minus;
            if (minus.Count == 0) return plus;
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
            // TraceBoundary takes the seed in the current UCS; the API speaks WCS
            Point3d seed = a.ReqPt("point").TransformBy(c.Ed.CurrentUserCoordinateSystem.Inverse());
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
                        if (!aid.IsErased && tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar && !existing.ContainsKey(ar.Tag))
                            existing[ar.Tag] = ar;
                    // an attribute can sit on its own (locked) layer: leave the whole insert alone then
                    if (existing.Values.Any(x => IsLocked(lockedCache, tr, x.LayerId))) { locked++; inserts--; continue; }

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
