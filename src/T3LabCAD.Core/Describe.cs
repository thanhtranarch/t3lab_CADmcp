using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using AcRx = Autodesk.AutoCAD.Runtime;
using static T3LabCAD.Core.Geo;

namespace T3LabCAD.Core
{
    /// <summary>Entity -> JSON-friendly dictionary. Summary keeps the key geometry; full adds styling and every vertex.</summary>
    internal static class Describe
    {
        const int SummaryVertexLimit = 8;
        const int MTextSummaryChars = 300;

        public static Dictionary<string, object> Entity(Entity e, Transaction tr, bool full)
        {
            var d = new Dictionary<string, object>
            {
                ["handle"] = e.Handle.ToString(),
                ["type"] = e.GetRXClass().DxfName,
                ["layer"] = e.Layer,
            };
            if (full)
            {
                d["class"] = e.GetType().Name;
                d["color"] = Acad.ColorOf(e.Color);
                d["linetype"] = e.Linetype;
                d["lineweight"] = Acad.LineWeightOf(e.LineWeight);
                if (!e.Visible) d["visible"] = false;
                var box = Box(e);
                if (box != null) d["bbox"] = box;
            }

            switch (e)
            {
                case Line l:
                    d["start"] = P(l.StartPoint);
                    d["end"] = P(l.EndPoint);
                    d["length"] = R(l.Length);
                    if (full) d["angle"] = Deg(l.Angle);
                    break;

                case Polyline pl:
                    d["closed"] = pl.Closed;
                    d["vertex_count"] = pl.NumberOfVertices;
                    d["length"] = R(pl.Length);
                    if (pl.Closed) d["area"] = R(pl.Area);
                    if (Math.Abs(pl.Elevation) > 1e-9) d["elevation"] = R(pl.Elevation);
                    if (full || pl.NumberOfVertices <= SummaryVertexLimit)
                    {
                        var vertices = new List<object>();
                        var bulges = new List<object>();
                        bool anyBulge = false;
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                        {
                            vertices.Add(P(pl.GetPoint2dAt(i)));
                            double b = pl.GetBulgeAt(i);
                            bulges.Add(R(b));
                            if (Math.Abs(b) > 1e-9) anyBulge = true;
                        }
                        d["vertices"] = vertices;
                        if (anyBulge) d["bulges"] = bulges;
                        if (full && pl.ConstantWidth > 0) d["width"] = R(pl.ConstantWidth);
                    }
                    break;

                case Circle c:
                    d["center"] = P(c.Center);
                    d["radius"] = R(c.Radius);
                    break;

                case Arc a:
                    d["center"] = P(a.Center);
                    d["radius"] = R(a.Radius);
                    d["start_angle"] = Deg(a.StartAngle);
                    d["end_angle"] = Deg(a.EndAngle);
                    d["length"] = R(a.Length);
                    break;

                case Ellipse el:
                    d["center"] = P(el.Center);
                    d["major_axis"] = V(el.MajorAxis);
                    d["ratio"] = R(el.RadiusRatio);
                    break;

                case AttributeDefinition ad:
                    d["tag"] = ad.Tag;
                    d["prompt"] = ad.Prompt;
                    d["default"] = ad.TextString;
                    d["position"] = P(Acad.TextPoint(ad));
                    d["height"] = R(ad.Height);
                    break;

                case DBText t:
                    d["text"] = t.TextString;
                    d["position"] = P(Acad.TextPoint(t));
                    d["height"] = R(t.Height);
                    if (Math.Abs(t.Rotation) > 1e-9) d["rotation"] = Deg(t.Rotation);
                    if (full) d["style"] = t.TextStyleName;
                    break;

                case MText mt:
                    string plain = mt.Text ?? "";
                    d["text"] = full || plain.Length <= MTextSummaryChars ? plain : plain.Substring(0, MTextSummaryChars) + "...";
                    d["position"] = P(mt.Location);
                    d["height"] = R(mt.TextHeight);
                    if (mt.Width > 0) d["width"] = R(mt.Width);
                    if (Math.Abs(mt.Rotation) > 1e-9) d["rotation"] = Deg(mt.Rotation);
                    if (full)
                    {
                        d["contents"] = mt.Contents;
                        d["attachment"] = mt.Attachment.ToString();
                        d["style"] = mt.TextStyleName;
                    }
                    break;

                case Table tb:
                    d["position"] = P(tb.Position);
                    d["rows"] = tb.Rows.Count;
                    d["columns"] = tb.Columns.Count;
                    break;

                case BlockReference br:
                    d["name"] = Acad.EffectiveName(br, tr);
                    d["position"] = P(br.Position);
                    if (Math.Abs(br.Rotation) > 1e-9) d["rotation"] = Deg(br.Rotation);
                    var s = br.ScaleFactors;
                    if (full || Math.Abs(s.X - 1) > 1e-9 || Math.Abs(s.Y - 1) > 1e-9 || Math.Abs(s.Z - 1) > 1e-9)
                        d["scale"] = new object[] { R(s.X), R(s.Y), R(s.Z) };
                    var attributes = Attributes(br, tr);
                    if (attributes.Count > 0) d["attributes"] = attributes;
                    if (full)
                    {
                        if (br.IsDynamicBlock) d["dynamic_properties"] = DynamicProperties(br);
                        var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        if (btr.IsFromExternalReference) d["xref_path"] = btr.PathName;
                    }
                    break;

                case Dimension dim:
                    d["dim_type"] = dim.GetType().Name;
                    try { d["measurement"] = R(dim.Measurement); } catch (AcRx.Exception) { }
                    if (!string.IsNullOrEmpty(dim.DimensionText)) d["text_override"] = dim.DimensionText;
                    d["style"] = dim.DimensionStyleName;
                    if (full)
                    {
                        if (dim is RotatedDimension rd)
                        {
                            d["p1"] = P(rd.XLine1Point); d["p2"] = P(rd.XLine2Point); d["dim_point"] = P(rd.DimLinePoint);
                            d["rotation"] = Deg(rd.Rotation);
                        }
                        else if (dim is AlignedDimension ald)
                        {
                            d["p1"] = P(ald.XLine1Point); d["p2"] = P(ald.XLine2Point); d["dim_point"] = P(ald.DimLinePoint);
                        }
                        d["text_position"] = P(dim.TextPosition);
                    }
                    break;

                case Hatch h:
                    d["pattern"] = h.PatternName;
                    try { d["area"] = R(h.Area); } catch (AcRx.Exception) { }
                    d["associative"] = h.Associative;
                    if (full) { d["pattern_scale"] = R(h.PatternScale); d["pattern_angle"] = Deg(h.PatternAngle); }
                    break;

                case DBPoint pt:
                    d["position"] = P(pt.Position);
                    break;

                case Spline sp:
                    d["degree"] = sp.Degree;
                    d["fit_points"] = sp.NumFitPoints;
                    d["control_points"] = sp.NumControlPoints;
                    d["closed"] = sp.Closed;
                    try { d["length"] = R(sp.GetDistanceAtParameter(sp.EndParam)); } catch (AcRx.Exception) { }
                    break;

                case MLeader ml:
                    if (ml.ContentType == ContentType.MTextContent && ml.MText != null)
                    {
                        d["text"] = ml.MText.Text;
                        d["position"] = P(ml.MText.Location);
                    }
                    else d["content"] = ml.ContentType.ToString();
                    break;

                case Viewport vp:
                    d["number"] = vp.Number;
                    d["center"] = P(vp.CenterPoint);
                    d["width"] = R(vp.Width);
                    d["height"] = R(vp.Height);
                    d["scale"] = vp.CustomScale > 0 ? R(1.0 / vp.CustomScale) : 0;
                    d["view_center"] = P(vp.ViewCenter);
                    d["locked"] = vp.Locked;
                    break;

                case Curve curve:
                    try { d["length"] = R(curve.GetDistanceAtParameter(curve.EndParam)); } catch (AcRx.Exception) { }
                    if (!full) AddBox(d, e);
                    break;

                default:
                    if (!full) AddBox(d, e);
                    break;
            }
            return d;
        }

        static void AddBox(Dictionary<string, object> d, Entity e)
        {
            var box = Box(e);
            if (box != null) d["bbox"] = box;
        }

        public static Dictionary<string, object> Attributes(BlockReference br, Transaction tr)
        {
            var result = new Dictionary<string, object>();
            foreach (ObjectId id in br.AttributeCollection)
            {
                if (id.IsErased) continue;
                if (tr.GetObject(id, OpenMode.ForRead) is AttributeReference ar) result[ar.Tag] = ar.TextString;
            }
            return result;
        }

        static Dictionary<string, object> DynamicProperties(BlockReference br)
        {
            var result = new Dictionary<string, object>();
            try
            {
                foreach (DynamicBlockReferenceProperty prop in br.DynamicBlockReferencePropertyCollection)
                {
                    if (prop.PropertyName == "Origin") continue;
                    object v = prop.Value;
                    result[prop.PropertyName] = v is double dv ? R(dv) : v is string || v is short || v is int || v is bool ? v : Convert.ToString(v);
                }
            }
            catch (AcRx.Exception)
            {
                // some dynamic blocks expose properties that cannot be read
            }
            return result;
        }
    }
}
