using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace T3LabCAD.Core
{
    /// <summary>edit_geometry extend / trim / break / fillet / chamfer (2D, in the XY plane, WCS).</summary>
    internal static class GeometryOps
    {
        const double Tol = 1e-8;

        static Dictionary<string, object> Info(Entity e)
        {
            var d = new Dictionary<string, object> { ["handle"] = e.Handle.ToString(), ["type"] = e.GetRXClass().DxfName };
            double? length = Metrics.Length(e);
            if (length.HasValue) d["length"] = Geo.R(length.Value);
            return d;
        }

        static List<Entity> Edges(ToolContext c, Args a)
        {
            if (!a.Has("boundary_handles")) throw new ToolError("Pass boundary_handles: the boundary / cutting edges.");
            return Acad.Ids(c.Db, a.Strs("boundary_handles")).Select(id => (Entity)c.Tr.GetObject(id, OpenMode.ForRead)).ToList();
        }

        static double Normalize(double rad)
        {
            double twoPi = 2 * Math.PI;
            rad %= twoPi;
            return rad < 0 ? rad + twoPi : rad;
        }

        static double AngleOf(Vector3d v) => Math.Atan2(v.Y, v.X);

        static ToolError Wrap(Exception ex) =>
            ex is ToolError te ? te : ex is AcRx.Exception ae
                ? new ToolError(ae.ErrorStatus == AcRx.ErrorStatus.OnLockedLayer ? "entity is on a locked layer" : "AutoCAD error " + ae.ErrorStatus)
                : new ToolError(ex.Message);

        static void DisposeUnused(IEnumerable<DBObject> objects)
        {
            foreach (DBObject o in objects)
                if (o != null && o.ObjectId.IsNull && !o.IsDisposed) o.Dispose();
        }

        // ---------------------------------------------------------------- extend

        public static object Extend(ToolContext c, Args a)
        {
            List<Entity> edges = Edges(c, a);
            string end = (a.Str("end") ?? "auto").Trim().ToLowerInvariant();
            if (end != "auto" && end != "start" && end != "end" && end != "both") throw new ToolError("end must be start, end, both or auto.");
            Point3d? pick = a.Has("pick_point") ? a.ReqPt("pick_point") : (Point3d?)null;

            var done = new List<object>();
            var errors = new List<object>();
            foreach (ObjectId id in Acad.Ids(c.Db, a.Strs("handles")))
            {
                try
                {
                    if (!(c.Tr.GetObject(id, OpenMode.ForRead) is Curve curve)) throw new ToolError("not a curve");
                    if (curve.Closed) throw new ToolError("a closed curve has no end to extend");
                    var ends = new List<bool>(); // true = start
                    if (end == "start" || end == "both") ends.Add(true);
                    if (end == "end" || end == "both") ends.Add(false);
                    if (end == "auto")
                    {
                        if (pick.HasValue) ends.Add(curve.StartPoint.DistanceTo(pick.Value) < curve.EndPoint.DistanceTo(pick.Value));
                        else { ends.Add(true); ends.Add(false); }
                    }
                    var extended = new List<object>();
                    foreach (bool atStart in ends)
                    {
                        Point3d? target = ExtendTarget(curve, atStart, edges);
                        if (!target.HasValue) continue;
                        if (!curve.IsWriteEnabled) curve.UpgradeOpen();
                        curve.Extend(atStart, target.Value);
                        extended.Add(atStart ? "start" : "end");
                    }
                    if (extended.Count == 0) throw new ToolError("no boundary edge lies in the way of its end(s)");
                    var info = Info(curve);
                    info["extended"] = extended;
                    done.Add(info);
                }
                catch (Exception ex) when (ex is ToolError || ex is AcRx.Exception)
                {
                    errors.Add(new Dictionary<string, object> { ["handle"] = id.Handle.ToString(), ["error"] = Wrap(ex).Message });
                }
            }
            if (done.Count == 0) throw new ToolError("Nothing was extended: " + Json.Write(errors));
            var result = new Dictionary<string, object> { ["operation"] = "extend", ["extended"] = done };
            if (errors.Count > 0) result["errors"] = errors;
            return result;
        }

        /// <summary>The nearest point beyond this end where the extended curve meets an edge; null when there is none.</summary>
        static Point3d? ExtendTarget(Curve curve, bool atStart, List<Entity> edges)
        {
            Point3d endPoint = atStart ? curve.StartPoint : curve.EndPoint;
            Vector3d dir = curve.GetFirstDerivative(atStart ? curve.StartParam : curve.EndParam);
            if (dir.Length < Tol) return null;
            dir = (atStart ? dir.Negate() : dir).GetNormal();
            var arc = curve as Arc;
            Point3d? best = null;
            double bestKey = double.MaxValue;
            foreach (Entity edge in edges)
            {
                if (edge.ObjectId == curve.ObjectId) continue;
                var pts = new Point3dCollection();
                try { curve.IntersectWith(edge, Intersect.ExtendThis, pts, IntPtr.Zero, IntPtr.Zero); }
                catch (AcRx.Exception) { continue; }
                foreach (Point3d p in pts)
                {
                    if (curve.GetClosestPointTo(p, false).DistanceTo(p) < 1e-6) continue; // already on the curve
                    double key;
                    if (arc != null)
                    {
                        double ang = AngleOf(p - arc.Center);
                        key = atStart ? Normalize(arc.StartAngle - ang) : Normalize(ang - arc.EndAngle);
                    }
                    else
                    {
                        key = (p - endPoint).DotProduct(dir);
                        if (key <= Tol) continue; // behind this end
                    }
                    if (key < bestKey) { bestKey = key; best = p; }
                }
            }
            return best;
        }

        // ---------------------------------------------------------------- trim

        public static object Trim(ToolContext c, Args a)
        {
            List<Entity> edges = Edges(c, a);
            List<ObjectId> ids = Acad.Ids(c.Db, a.Strs("handles"));
            List<Point3d> picks = a.Has("pick_points") ? a.Pts("pick_points") : a.Has("pick_point") ? new List<Point3d> { a.ReqPt("pick_point") } : null;
            if (picks == null || (picks.Count != ids.Count && picks.Count != 1))
                throw new ToolError("Pass pick_point (a point on the part to remove), or pick_points with one point per handle.");

            var done = new List<object>();
            var errors = new List<object>();
            for (int i = 0; i < ids.Count; i++)
            {
                ObjectId id = ids[i];
                Point3d pick = picks[picks.Count == 1 ? 0 : i];
                var pieces = new List<DBObject>();
                try
                {
                    if (!(c.Tr.GetObject(id, OpenMode.ForRead) is Curve curve)) throw new ToolError("not a curve");
                    var parameters = new List<double>();
                    foreach (Entity edge in edges)
                    {
                        if (edge.ObjectId == id) continue;
                        var pts = new Point3dCollection();
                        try { curve.IntersectWith(edge, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero); }
                        catch (AcRx.Exception) { continue; }
                        foreach (Point3d p in pts)
                        {
                            double t;
                            try { t = curve.GetParameterAtPoint(curve.GetClosestPointTo(p, false)); }
                            catch (AcRx.Exception) { continue; }
                            if (!curve.Closed && (Math.Abs(t - curve.StartParam) < 1e-9 || Math.Abs(t - curve.EndParam) < 1e-9)) continue;
                            if (!parameters.Any(q => Math.Abs(q - t) < 1e-9)) parameters.Add(t);
                        }
                    }
                    parameters.Sort();
                    if (parameters.Count == 0) throw new ToolError("no cutting edge crosses it");
                    if (curve.Closed && parameters.Count < 2) throw new ToolError("a closed curve needs at least 2 crossings to trim");

                    pieces.AddRange(curve.GetSplitCurves(new DoubleCollection(parameters.ToArray())).Cast<DBObject>());
                    var curves = pieces.OfType<Curve>().ToList();
                    if (curves.Count < 2) throw new ToolError("the cutting edges do not split it");
                    Curve removed = curves.OrderBy(cv => cv.GetClosestPointTo(pick, false).DistanceTo(pick)).First();
                    var space = (BlockTableRecord)c.Tr.GetObject(curve.OwnerId, OpenMode.ForWrite);
                    curve.UpgradeOpen();
                    var kept = new List<object>();
                    foreach (Curve piece in curves)
                    {
                        if (piece == removed) continue;
                        Acad.Append(space, piece, c.Tr);
                        kept.Add(Info(piece));
                    }
                    double removedLength = Metrics.Length(removed) ?? 0;
                    curve.Erase();
                    done.Add(new Dictionary<string, object>
                    {
                        ["handle"] = id.Handle.ToString(),
                        ["removed_length"] = Geo.R(removedLength),
                        ["remaining"] = kept,
                    });
                }
                catch (Exception ex) when (ex is ToolError || ex is AcRx.Exception)
                {
                    errors.Add(new Dictionary<string, object> { ["handle"] = id.Handle.ToString(), ["error"] = Wrap(ex).Message });
                }
                finally
                {
                    DisposeUnused(pieces);
                }
            }
            if (done.Count == 0) throw new ToolError("Nothing was trimmed: " + Json.Write(errors));
            var result = new Dictionary<string, object> { ["operation"] = "trim", ["trimmed"] = done };
            if (errors.Count > 0) result["errors"] = errors;
            return result;
        }

        // ---------------------------------------------------------------- break

        public static object Break(ToolContext c, Args a)
        {
            List<ObjectId> ids = Acad.Ids(c.Db, a.Strs("handles"));
            if (ids.Count != 1) throw new ToolError("break takes one handle.");
            if (!(c.Tr.GetObject(ids[0], OpenMode.ForRead) is Curve curve)) throw new ToolError("break needs a curve.");
            Point3d p1 = curve.GetClosestPointTo(a.ReqPt("point"), false);
            double t1 = curve.GetParameterAtPoint(p1);
            var pieces = new List<DBObject>();
            try
            {
                List<Curve> keep;
                if (!a.Has("point2"))
                {
                    if (curve.Closed) throw new ToolError("A closed curve needs point and point2 (the part between them is removed).");
                    if (Math.Abs(t1 - curve.StartParam) < 1e-9 || Math.Abs(t1 - curve.EndParam) < 1e-9)
                        throw new ToolError("The break point is at an end of the curve.");
                    pieces.AddRange(curve.GetSplitCurves(new DoubleCollection(new[] { t1 })).Cast<DBObject>());
                    keep = pieces.OfType<Curve>().ToList();
                }
                else
                {
                    Point3d p2 = curve.GetClosestPointTo(a.ReqPt("point2"), false);
                    double t2 = curve.GetParameterAtPoint(p2);
                    if (Math.Abs(t1 - t2) < 1e-9) throw new ToolError("point and point2 are the same place on the curve.");
                    var split = new[] { t1, t2 }.Where(t => curve.Closed || (t - curve.StartParam > 1e-9 && curve.EndParam - t > 1e-9))
                        .OrderBy(t => t).ToArray();
                    if (split.Length == 0) throw new ToolError("Both points are at the ends: that would remove the whole curve.");
                    pieces.AddRange(curve.GetSplitCurves(new DoubleCollection(split)).Cast<DBObject>());
                    var curves = pieces.OfType<Curve>().ToList();
                    // removed: the piece running from point to point2 along the curve (counter-clockwise on a circle, like BREAK)
                    Curve removed = curve.Closed
                        ? curves.OrderBy(cv => cv.StartPoint.DistanceTo(p1) + cv.EndPoint.DistanceTo(p2)).First()
                        : curves.OrderBy(cv => Math.Abs(MidParameter(curve, cv) - (t1 + t2) / 2)).First();
                    keep = curves.Where(cv => cv != removed).ToList();
                }
                var space = (BlockTableRecord)c.Tr.GetObject(curve.OwnerId, OpenMode.ForWrite);
                curve.UpgradeOpen();
                var made = new List<object>();
                foreach (Curve piece in keep)
                {
                    Acad.Append(space, piece, c.Tr);
                    made.Add(Info(piece));
                }
                curve.Erase();
                return new Dictionary<string, object> { ["operation"] = "break", ["handle"] = ids[0].Handle.ToString(), ["pieces"] = made };
            }
            finally
            {
                DisposeUnused(pieces);
            }
        }

        /// <summary>Parameter on the original curve at the middle of a piece.</summary>
        static double MidParameter(Curve original, Curve piece)
        {
            double length = Metrics.Length(piece) ?? 0;
            Point3d mid = piece.GetPointAtDist(length / 2);
            return original.GetParameterAtPoint(original.GetClosestPointTo(mid, false));
        }

        // ---------------------------------------------------------------- fillet / chamfer

        public static object Corner(ToolContext c, Args a, bool fillet)
        {
            string op = fillet ? "fillet" : "chamfer";
            double radius = fillet ? a.Num("radius", 0) : 0;
            double d1 = fillet ? 0 : a.ReqNum("distance");
            double d2 = fillet ? 0 : a.Num("distance2", d1);
            if (radius < 0 || d1 < 0 || d2 < 0) throw new ToolError("radius / distance cannot be negative.");
            List<ObjectId> ids = Acad.Ids(c.Db, a.Strs("handles"));
            if (ids.Count == 1 && c.Tr.GetObject(ids[0], OpenMode.ForRead) is Polyline pl) return PolylineCorners(c, pl, fillet, radius, d1, d2);
            if (ids.Count != 2) throw new ToolError($"{op} takes 2 lines, or 1 polyline (every corner).");
            var l1 = c.Tr.GetObject(ids[0], OpenMode.ForWrite) as Line ?? throw new ToolError($"{op}: the first handle is not a line.");
            var l2 = c.Tr.GetObject(ids[1], OpenMode.ForWrite) as Line ?? throw new ToolError($"{op}: the second handle is not a line.");
            foreach (Line l in new[] { l1, l2 })
                if (Math.Abs(l.StartPoint.Z) > 1e-9 || Math.Abs(l.EndPoint.Z) > 1e-9) throw new ToolError($"{op} works on lines in the XY plane (Z = 0).");

            Point3d[] hits = new Line3d(l1.StartPoint, l1.EndPoint).IntersectWith(new Line3d(l2.StartPoint, l2.EndPoint));
            if (hits == null || hits.Length == 0) throw new ToolError("The lines are parallel.");
            Point3d x = hits[0];
            bool start1 = l1.StartPoint.DistanceTo(x) < l1.EndPoint.DistanceTo(x);
            bool start2 = l2.StartPoint.DistanceTo(x) < l2.EndPoint.DistanceTo(x);
            Point3d far1 = start1 ? l1.EndPoint : l1.StartPoint, far2 = start2 ? l2.EndPoint : l2.StartPoint;
            double len1 = far1.DistanceTo(x), len2 = far2.DistanceTo(x);
            if (len1 < Tol || len2 < Tol) throw new ToolError("A line has no length on its side of the corner.");
            Vector3d u1 = (far1 - x).GetNormal(), u2 = (far2 - x).GetNormal();
            double theta = u1.GetAngleTo(u2);
            if (theta < 1e-6 || theta > Math.PI - 1e-6) throw new ToolError("The lines are collinear.");

            void SetNear(Line l, bool atStart, Point3d p)
            {
                if (atStart) l.StartPoint = p; else l.EndPoint = p;
            }

            var space = (BlockTableRecord)c.Tr.GetObject(l1.OwnerId, OpenMode.ForWrite);
            var result = new Dictionary<string, object> { ["operation"] = op, ["corner"] = Geo.P(x) };
            if (fillet && radius < Tol)
            {
                SetNear(l1, start1, x);
                SetNear(l2, start2, x);
            }
            else if (fillet)
            {
                double t = radius / Math.Tan(theta / 2);
                if (t > len1 + 1e-9 || t > len2 + 1e-9) throw new ToolError($"Radius {radius} is too large for these lines (needs {Geo.R(t)} of each line).");
                Point3d t1 = x + u1 * t, t2 = x + u2 * t;
                Point3d center = x + (u1 + u2).GetNormal() * (radius / Math.Sin(theta / 2));
                double a1 = AngleOf(t1 - center), a2 = AngleOf(t2 - center);
                double sweep = Math.PI - theta;
                bool forward = Math.Abs(Normalize(a2 - a1) - sweep) < 1e-6;
                var arc = new Arc(center, radius, forward ? a1 : a2, forward ? a2 : a1);
                arc.SetPropertiesFrom(l1);
                SetNear(l1, start1, t1);
                SetNear(l2, start2, t2);
                Acad.Append(space, arc, c.Tr);
                result["arc"] = arc.Handle.ToString();
            }
            else
            {
                if (d1 > len1 + 1e-9 || d2 > len2 + 1e-9) throw new ToolError("The chamfer distances are longer than the lines.");
                Point3d t1 = x + u1 * d1, t2 = x + u2 * d2;
                var line = new Line(t1, t2);
                line.SetPropertiesFrom(l1);
                SetNear(l1, start1, t1);
                SetNear(l2, start2, t2);
                Acad.Append(space, line, c.Tr);
                result["line"] = line.Handle.ToString();
            }
            result["lines"] = new object[] { Info(l1), Info(l2) };
            return result;
        }

        /// <summary>Fillet / chamfer every corner between two straight segments of a polyline.</summary>
        static object PolylineCorners(ToolContext c, Polyline pl, bool fillet, double r, double d1, double d2)
        {
            int n = pl.NumberOfVertices;
            bool closed = pl.Closed;
            var pts = Enumerable.Range(0, n).Select(i => pl.GetPoint2dAt(i)).ToList();
            var bulges = Enumerable.Range(0, n).Select(i => pl.GetBulgeAt(i)).ToList();
            var outPts = new List<Point2d>();
            var outBulges = new List<double>();
            int done = 0, skipped = 0;
            for (int i = 0; i < n; i++)
            {
                bool corner = closed || (i > 0 && i < n - 1);
                int prev = (i - 1 + n) % n, next = (i + 1) % n;
                if (!corner || Math.Abs(bulges[prev]) > 1e-9 || Math.Abs(bulges[i]) > 1e-9)
                {
                    if (corner) skipped++; // an arc segment touches this corner
                    outPts.Add(pts[i]);
                    outBulges.Add(bulges[i]);
                    continue;
                }
                Vector2d vin = pts[prev] - pts[i], vout = pts[next] - pts[i];
                double lenIn = vin.Length, lenOut = vout.Length;
                double theta = lenIn < Tol || lenOut < Tol ? 0 : vin.GetAngleTo(vout);
                if (theta < 1e-6 || theta > Math.PI - 1e-6)
                {
                    outPts.Add(pts[i]);
                    outBulges.Add(bulges[i]);
                    continue; // no corner (straight through or a zero-length segment)
                }
                double ta = fillet ? r / Math.Tan(theta / 2) : d1, tb = fillet ? ta : d2;
                // a segment is shared with the neighbouring corner: use at most half of it, all of it next to an open end
                double maxIn = !closed && prev == 0 ? lenIn : lenIn / 2, maxOut = !closed && next == n - 1 ? lenOut : lenOut / 2;
                if (ta > maxIn + 1e-9 || tb > maxOut + 1e-9 || (fillet && r < Tol))
                {
                    skipped++;
                    outPts.Add(pts[i]);
                    outBulges.Add(bulges[i]);
                    continue;
                }
                Point2d t1 = pts[i] + vin.GetNormal() * ta, t2 = pts[i] + vout.GetNormal() * tb;
                double bulge = 0;
                if (fillet)
                {
                    Vector2d travelIn = pts[i] - pts[prev], travelOut = pts[next] - pts[i];
                    double cross = travelIn.X * travelOut.Y - travelIn.Y * travelOut.X; // > 0: left (counter-clockwise) turn
                    bulge = Math.Tan((Math.PI - theta) / 4) * Math.Sign(cross);
                }
                outPts.Add(t1);
                outBulges.Add(bulge);
                outPts.Add(t2);
                outBulges.Add(bulges[i]);
                done++;
            }
            if (done == 0) throw new ToolError("No corner could be " + (fillet ? "filleted" : "chamfered") +
                                               (fillet && r < Tol ? ": pass radius > 0." : ": the segments are too short or curved."));
            pl.UpgradeOpen();
            for (int i = 0; i < outPts.Count; i++)
            {
                if (i < pl.NumberOfVertices)
                {
                    pl.SetPointAt(i, outPts[i]);
                    pl.SetBulgeAt(i, outBulges[i]);
                }
                else pl.AddVertexAt(i, outPts[i], outBulges[i], 0, 0);
            }
            var result = Info(pl);
            result["operation"] = fillet ? "fillet" : "chamfer";
            result["corners"] = done;
            if (skipped > 0) result["corners_skipped"] = skipped;
            double? area = Metrics.Area(pl);
            if (area.HasValue) result["area"] = Geo.R(area.Value);
            return result;
        }
    }
}
