using System;
using Autodesk.AutoCAD.DatabaseServices;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace T3LabCAD.Core
{
    /// <summary>Length and area of an entity, shared by query_entities, measure and quantity_takeoff.</summary>
    internal static class Metrics
    {
        /// <summary>Curve length or region perimeter; null for infinite or unmeasurable entities.</summary>
        public static double? Length(Entity e)
        {
            try
            {
                switch (e)
                {
                    case Ray _:
                    case Xline _:
                        return null;
                    case Curve c:
                        return Math.Abs(c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam));
                    case Region r:
                        return r.Perimeter;
                }
            }
            catch (AcRx.Exception)
            {
                // degenerate geometry
            }
            return null;
        }

        /// <summary>Area of closed curves (also a polyline whose last vertex is its first), hatches and regions; null otherwise.</summary>
        public static double? Area(Entity e)
        {
            try
            {
                switch (e)
                {
                    case Hatch h: return h.Area;
                    case Region r: return r.Area;
                    case Polyline pl when pl.Closed || (pl.NumberOfVertices > 2 && pl.GetPoint2dAt(0).IsEqualTo(pl.GetPoint2dAt(pl.NumberOfVertices - 1))):
                        return pl.Area;
                    case Curve c when c.Closed: return c.Area;
                }
            }
            catch (AcRx.Exception)
            {
                // e.g. a hatch whose loops cannot be evaluated
            }
            return null;
        }
    }
}
