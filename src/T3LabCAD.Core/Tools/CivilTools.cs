using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>
    /// Point clouds (PointCloudEx, in the AutoCAD API) and Civil 3D objects. Civil 3D is reached by late binding
    /// (reflection + dynamic) so the add-in needs no Civil 3D reference and still loads in plain AutoCAD,
    /// where these tools answer that Civil 3D is not running.
    /// </summary>
    internal static class CivilTools
    {
        static readonly string[] QueryKinds = { "summary", "alignment", "profile", "surface", "pipe_network", "cogo_point" };

        public static void Register()
        {
            Registry.Add("attach_point_cloud", "Attach point cloud", ToolKind.Write,
                "Attach a ReCap point cloud (.rcp project or .rcs scan) for Scan-to-BIM tracing, like POINTCLOUDATTACH. " +
                "Returns its handle and extents (zoom handles=[...] to see it).",
                ObjR("path",
                    P("path", Str(".rcp or .rcs file")),
                    P("position", Pt("Insertion point (default [0, 0, 0])")),
                    P("scale", Num("Scale (default 1)")),
                    P("rotation", Num("Rotation in degrees (default 0)")),
                    P("layer", Str("Layer (created if missing)"))),
                AttachPointCloud, tx: false);

            Registry.Add("civil_query", "Civil 3D objects", ToolKind.Read,
                "Civil 3D only. kind: summary (counts), alignment (length, stations), profile, surface (elevation range, points), " +
                "pipe_network (pipes / structures), cogo_point (number, name, E / N / Z, description). " +
                "elevation_at {surface, points} gives the surface elevation at each XY; station_offset {alignment, points} gives station and offset.",
                Obj(P("kind", Enum("What to list (default summary)", QueryKinds)),
                    P("name", Str("Wildcard filter on names")),
                    P("limit", Int("Max items (default 200, cogo points 1000)")),
                    P("elevation_at", ObjAny("{surface: name, points: [[x, y], ...]}")),
                    P("station_offset", ObjAny("{alignment: name, points: [[x, y], ...]}"))),
                CivilQuery);

            Registry.Add("civil_points", "Civil 3D points and surface", ToolKind.Write,
                "Civil 3D only. Create COGO points from points [[x, y, z], ...] (next point numbers, optional description), and / or " +
                "add the points to a TIN surface (created when missing).",
                ObjR("points",
                    P("points", Pts("[[x, y, z], ...]")),
                    P("description", Str("Raw description for the new COGO points")),
                    P("cogo", Bool("Create COGO points (default true)")),
                    P("surface", Str("Also add the points as vertices of this TIN surface (created if missing)"))),
                CivilPoints);
        }

        // ---------------------------------------------------------------- point cloud

        static object AttachPointCloud(ToolContext c, Args a)
        {
            var db = c.Db;
            string path = DocumentTools.ResolvePath(a.ReqStr("path"));
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".rcp" && ext != ".rcs") throw new ToolError("Attach a ReCap .rcp project or .rcs scan (convert other formats in ReCap first).");
            if (!File.Exists(path)) throw new ToolError($"File not found: {path}");
            double scale = a.Num("scale", 1);
            if (scale <= 0) throw new ToolError("scale must be greater than 0.");

            ObjectId id = PointCloudEx.AttachPointCloud(path, a.Pt("position", Point3d.Origin), scale, Geo.Rad(a.Num("rotation", 0)), db);
            if (id.IsNull) throw new ToolError($"AutoCAD could not attach {Path.GetFileName(path)} (is the point cloud engine available?).");
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var cloud = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                if (a.Has("layer")) cloud.LayerId = Acad.EnsureLayer(db, tr, a.ReqStr("layer"));
                var result = new Dictionary<string, object> { ["handle"] = cloud.Handle.ToString(), ["file"] = path };
                if (cloud is PointCloudEx pc)
                {
                    try
                    {
                        Extents3d ext3 = pc.GeomExtents;
                        result["extents"] = new[] { Geo.P(ext3.MinPoint), Geo.P(ext3.MaxPoint) };
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { /* extents come once the cloud is loaded */ }
                }
                tr.Commit();
                c.NeedsRegen = true;
                return result;
            }
        }

        // ---------------------------------------------------------------- Civil 3D (late bound)

        static Type CivilType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(asm =>
            {
                try { return asm.GetType(fullName, false); }
                catch (Exception) { return null; }
            }).FirstOrDefault(t => t != null);

        static dynamic CivilDocument()
        {
            Type app = CivilType("Autodesk.Civil.ApplicationServices.CivilApplication")
                ?? throw new ToolError("Civil 3D is not running: civil_query / civil_points need AutoCAD Civil 3D.");
            object doc = app.GetProperty("ActiveDocument")?.GetValue(null, null);
            return doc ?? throw new ToolError("No Civil 3D document is active.");
        }

        /// <summary>Read one property / call through dynamic; null when Civil 3D refuses it.</summary>
        static object Try(Func<object> read)
        {
            try
            {
                object v = read();
                return v is double d ? Geo.R(d) : v;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static IEnumerable<ObjectId> Ids(object collection)
        {
            if (collection is IEnumerable items)
                foreach (object o in items)
                    if (o is ObjectId id && !id.IsErased) yield return id;
        }

        static string NameOf(dynamic o) => Convert.ToString(Try(() => o.Name)) ?? "";

        static ObjectId FindByName(IEnumerable<ObjectId> ids, Transaction tr, string name, string what)
        {
            foreach (ObjectId id in ids)
            {
                dynamic o = tr.GetObject(id, OpenMode.ForRead);
                if (string.Equals(NameOf(o), name, StringComparison.OrdinalIgnoreCase)) return id;
            }
            throw new ToolError($"No {what} named '{name}' (civil_query kind={what}).");
        }

        static object CivilQuery(ToolContext c, Args a)
        {
            var tr = c.Tr;
            dynamic doc = CivilDocument();
            string filter = a.Str("name");
            bool Keep(string n) => filter == null || Wild.Match(n, filter);

            if (a.Has("elevation_at"))
            {
                var q = a.Obj("elevation_at");
                ObjectId sid = FindByName(Ids(doc.GetSurfaceIds()), tr, q.ReqStr("surface"), "surface");
                dynamic surface = tr.GetObject(sid, OpenMode.ForRead);
                var rows = q.Pts("points").Select(p => (object)new Dictionary<string, object>
                {
                    ["point"] = Geo.P(new Point3d(p.X, p.Y, 0)),
                    ["elevation"] = Try(() => (double)surface.FindElevationAtXY(p.X, p.Y)), // null outside the surface
                }).ToList();
                return new Dictionary<string, object> { ["surface"] = NameOf(surface), ["elevations"] = rows };
            }
            if (a.Has("station_offset"))
            {
                var q = a.Obj("station_offset");
                ObjectId aid = FindByName(Ids(doc.GetAlignmentIds()), tr, q.ReqStr("alignment"), "alignment");
                dynamic alignment = tr.GetObject(aid, OpenMode.ForRead);
                var rows = new List<object>();
                foreach (Point3d p in q.Pts("points"))
                {
                    var row = new Dictionary<string, object> { ["point"] = Geo.P(new Point3d(p.X, p.Y, 0)) };
                    try
                    {
                        double station = 0, offset = 0;
                        alignment.StationOffset(p.X, p.Y, ref station, ref offset);
                        row["station"] = Geo.R(station);
                        row["offset"] = Geo.R(offset);
                    }
                    catch (Exception) { row["error"] = "outside the alignment"; }
                    rows.Add(row);
                }
                return new Dictionary<string, object> { ["alignment"] = NameOf(alignment), ["results"] = rows };
            }

            string kind = (a.Str("kind") ?? "summary").Trim().ToLowerInvariant();
            int limit = Math.Max(1, a.Int("limit", kind == "cogo_point" ? 1000 : 200));
            var items = new List<object>();
            switch (kind)
            {
                case "summary":
                    return new Dictionary<string, object>
                    {
                        ["alignments"] = Ids(doc.GetAlignmentIds()).Count(),
                        ["surfaces"] = Ids(doc.GetSurfaceIds()).Count(),
                        ["pipe_networks"] = Ids(Try(() => doc.GetPipeNetworkIds())).Count(),
                        ["cogo_points"] = Try(() => (int)doc.CogoPoints.Count),
                    };
                case "alignment":
                case "profile":
                    foreach (ObjectId id in Ids(doc.GetAlignmentIds()))
                    {
                        dynamic al = tr.GetObject(id, OpenMode.ForRead);
                        if (kind == "alignment")
                        {
                            if (!Keep(NameOf(al))) continue;
                            items.Add(new Dictionary<string, object>
                            {
                                ["name"] = NameOf(al),
                                ["handle"] = id.Handle.ToString(),
                                ["length"] = Try(() => (double)al.Length),
                                ["start_station"] = Try(() => (double)al.StartingStation),
                                ["end_station"] = Try(() => (double)al.EndingStation),
                                ["profiles"] = Ids(Try(() => al.GetProfileIds())).Count(),
                                ["description"] = Try(() => (string)al.Description),
                            });
                        }
                        else
                        {
                            foreach (ObjectId pid in Ids(Try(() => al.GetProfileIds())))
                            {
                                dynamic pr = tr.GetObject(pid, OpenMode.ForRead);
                                if (!Keep(NameOf(pr))) continue;
                                items.Add(new Dictionary<string, object>
                                {
                                    ["name"] = NameOf(pr),
                                    ["alignment"] = NameOf(al),
                                    ["handle"] = pid.Handle.ToString(),
                                    ["start_station"] = Try(() => (double)pr.StartingStation),
                                    ["end_station"] = Try(() => (double)pr.EndingStation),
                                    ["min_elevation"] = Try(() => (double)pr.ElevationMin),
                                    ["max_elevation"] = Try(() => (double)pr.ElevationMax),
                                });
                            }
                        }
                        if (items.Count >= limit) break;
                    }
                    break;
                case "surface":
                    foreach (ObjectId id in Ids(doc.GetSurfaceIds()))
                    {
                        dynamic s = tr.GetObject(id, OpenMode.ForRead);
                        if (!Keep(NameOf(s))) continue;
                        dynamic props = Try(() => s.GetGeneralProperties());
                        items.Add(new Dictionary<string, object>
                        {
                            ["name"] = NameOf(s),
                            ["handle"] = id.Handle.ToString(),
                            ["type"] = ((object)s).GetType().Name,
                            ["min_elevation"] = props == null ? null : Try(() => (double)props.MinimumElevation),
                            ["max_elevation"] = props == null ? null : Try(() => (double)props.MaximumElevation),
                            ["mean_elevation"] = props == null ? null : Try(() => (double)props.MeanElevation),
                            ["points"] = props == null ? null : Try(() => Convert.ToInt64(props.NumberOfPoints)),
                        });
                        if (items.Count >= limit) break;
                    }
                    break;
                case "pipe_network":
                    foreach (ObjectId id in Ids(Try(() => doc.GetPipeNetworkIds())))
                    {
                        dynamic n = tr.GetObject(id, OpenMode.ForRead);
                        if (!Keep(NameOf(n))) continue;
                        items.Add(new Dictionary<string, object>
                        {
                            ["name"] = NameOf(n),
                            ["handle"] = id.Handle.ToString(),
                            ["pipes"] = Ids(Try(() => n.GetPipeIds())).Count(),
                            ["structures"] = Ids(Try(() => n.GetStructureIds())).Count(),
                        });
                        if (items.Count >= limit) break;
                    }
                    break;
                case "cogo_point":
                    foreach (ObjectId id in Ids(doc.CogoPoints))
                    {
                        dynamic p = tr.GetObject(id, OpenMode.ForRead);
                        string pointName = Convert.ToString(Try(() => p.PointName)) ?? "";
                        if (filter != null && !Wild.Match(pointName, filter) && !Wild.Match(Convert.ToString(Try(() => p.PointNumber)) ?? "", filter)) continue;
                        items.Add(new Dictionary<string, object>
                        {
                            ["number"] = Try(() => Convert.ToInt64(p.PointNumber)),
                            ["name"] = pointName,
                            ["handle"] = id.Handle.ToString(),
                            ["easting"] = Try(() => (double)p.Easting),
                            ["northing"] = Try(() => (double)p.Northing),
                            ["elevation"] = Try(() => (double)p.Elevation),
                            ["description"] = Try(() => (string)p.RawDescription),
                        });
                        if (items.Count >= limit) break;
                    }
                    break;
                default:
                    throw new ToolError($"kind must be one of: {string.Join(", ", QueryKinds)}.");
            }
            return new Dictionary<string, object> { ["kind"] = kind, ["count"] = items.Count, ["items"] = items };
        }

        static object CivilPoints(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            dynamic doc = CivilDocument();
            List<Point3d> pts = a.Pts("points");
            if (pts.Count == 0) throw new ToolError("'points' is empty.");
            var result = new Dictionary<string, object>();

            if (a.Bool("cogo", true))
            {
                dynamic cogo = doc.CogoPoints;
                string description = a.Str("description");
                var numbers = new List<object>();
                foreach (Point3d p in pts)
                {
                    ObjectId id = (ObjectId)cogo.Add(p, true);
                    dynamic point = tr.GetObject(id, OpenMode.ForWrite);
                    if (description != null) point.RawDescription = description;
                    numbers.Add(Try(() => Convert.ToInt64(point.PointNumber)));
                }
                result["cogo_points"] = numbers;
            }

            if (a.Has("surface"))
            {
                string name = a.ReqStr("surface").Trim();
                ObjectId sid = ObjectId.Null;
                foreach (ObjectId id in Ids(doc.GetSurfaceIds()))
                {
                    dynamic s = tr.GetObject(id, OpenMode.ForRead);
                    if (string.Equals(NameOf(s), name, StringComparison.OrdinalIgnoreCase)) { sid = id; break; }
                }
                bool created = false;
                if (sid.IsNull)
                {
                    Type tin = CivilType("Autodesk.Civil.DatabaseServices.TinSurface")
                        ?? throw new ToolError("Civil 3D TinSurface type not found.");
                    var create = tin.GetMethod("Create", new[] { typeof(Database), typeof(string) })
                        ?? throw new ToolError("This Civil 3D version has no TinSurface.Create(Database, string).");
                    sid = (ObjectId)create.Invoke(null, new object[] { db, name });
                    created = true;
                }
                dynamic surface = tr.GetObject(sid, OpenMode.ForWrite);
                if (((object)surface).GetType().Name != "TinSurface") throw new ToolError($"Surface '{name}' is not a TIN surface.");
                surface.AddVertices(new Point3dCollection(pts.ToArray()));
                result["surface"] = new Dictionary<string, object> { ["name"] = name, ["created"] = created, ["vertices_added"] = pts.Count };
            }
            if (result.Count == 0) throw new ToolError("Nothing to do: cogo=false and no surface.");
            c.NeedsRegen = true;
            return result;
        }
    }
}
