using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace T3LabCAD.Core
{
    /// <summary>Number / point formatting for results.</summary>
    internal static class Geo
    {
        public static double R(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 4);

        public static object P(Point3d p) =>
            Math.Abs(p.Z) < 1e-9 ? new object[] { R(p.X), R(p.Y) } : new object[] { R(p.X), R(p.Y), R(p.Z) };

        public static object P(Point2d p) => new object[] { R(p.X), R(p.Y) };

        public static object V(Vector3d v) =>
            Math.Abs(v.Z) < 1e-9 ? new object[] { R(v.X), R(v.Y) } : new object[] { R(v.X), R(v.Y), R(v.Z) };

        public static double Deg(double rad) => R(rad * 180.0 / Math.PI);

        public static double Rad(double deg) => deg * Math.PI / 180.0;

        public static object Box(Entity e)
        {
            Extents3d? b = e.Bounds;
            return b.HasValue ? new object[] { P(b.Value.MinPoint), P(b.Value.MaxPoint) } : null;
        }
    }

    /// <summary>AutoCAD-style wildcards (* and ?), case-insensitive.</summary>
    internal static class Wild
    {
        static readonly Dictionary<string, Regex> Cache = new Dictionary<string, Regex>();

        public static bool Any(string text, IList<string> patterns)
        {
            if (patterns == null || patterns.Count == 0) return true;
            foreach (var p in patterns)
                if (Match(text, p)) return true;
            return false;
        }

        public static bool Match(string text, string pattern)
        {
            text = text ?? "";
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
            Regex rx;
            lock (Cache)
            {
                if (!Cache.TryGetValue(pattern, out rx))
                {
                    rx = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
                    Cache[pattern] = rx;
                }
            }
            return rx.IsMatch(text);
        }
    }

    internal static class Acad
    {
        public static ObjectId SpaceId(Database db, Transaction tr, string space)
        {
            if (string.IsNullOrWhiteSpace(space) || space.Equals("current", StringComparison.OrdinalIgnoreCase)) return db.CurrentSpaceId;
            if (space.Equals("model", StringComparison.OrdinalIgnoreCase)) return SymbolUtilityServices.GetBlockModelSpaceId(db);
            if (space.Equals("paper", StringComparison.OrdinalIgnoreCase)) return SymbolUtilityServices.GetBlockPaperSpaceId(db);
            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (entry.Key.Equals(space, StringComparison.OrdinalIgnoreCase))
                    return ((Layout)tr.GetObject(entry.Value, OpenMode.ForRead)).BlockTableRecordId;
            }
            throw new ToolError($"Unknown space '{space}'. Use model, paper, current or a layout name (see list_layouts).");
        }

        public static string SpaceName(Transaction tr, ObjectId spaceId)
        {
            var btr = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
            if (!btr.IsLayout) return btr.Name;
            return ((Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead)).LayoutName;
        }

        public static bool TryId(Database db, string handle, out ObjectId id)
        {
            id = ObjectId.Null;
            if (!long.TryParse((handle ?? "").Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)) return false;
            return db.TryGetObjectId(new Handle(value), out id) && id.IsValid && !id.IsErased;
        }

        public static ObjectId Id(Database db, string handle)
        {
            if (!TryId(db, handle, out ObjectId id)) throw new ToolError($"No object with handle '{handle}' in this drawing.");
            return id;
        }

        public static List<ObjectId> Ids(Database db, IEnumerable<string> handles)
        {
            var ids = new List<ObjectId>();
            foreach (var h in handles) ids.Add(Id(db, h));
            if (ids.Count == 0) throw new ToolError("'handles' is empty.");
            return ids;
        }

        public static void ValidateName(string name, string what)
        {
            try { SymbolUtilityServices.ValidateSymbolName(name, false); }
            catch (AcRx.Exception) { throw new ToolError($"'{name}' is not a valid {what} name."); }
        }

        public static ObjectId EnsureLayer(Database db, Transaction tr, string name)
        {
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return table[name];
            ValidateName(name, "layer");
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        static bool IsSpecialLinetype(string name) =>
            name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Continuous", StringComparison.OrdinalIgnoreCase);

        public static void EnsureLinetype(Database db, Transaction tr, string name)
        {
            if (IsSpecialLinetype(name)) return;
            var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (table.Has(name)) return;
            foreach (string file in new[] { "acadiso.lin", "acad.lin" })
            {
                try { db.LoadLineTypeFile(name, file); }
                catch (AcRx.Exception) { continue; }
                table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                if (table.Has(name)) return;
            }
            throw new ToolError($"Linetype '{name}' is not loaded and is not in acadiso.lin / acad.lin.");
        }

        public static ObjectId LinetypeId(Database db, Transaction tr, string name)
        {
            EnsureLinetype(db, tr, name);
            var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            return table[name];
        }

        public static ObjectId TextStyle(Database db, Transaction tr, string name)
        {
            if (string.IsNullOrEmpty(name)) return db.Textstyle;
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (!table.Has(name)) throw new ToolError($"Text style '{name}' does not exist.");
            return table[name];
        }

        public static ObjectId DimStyle(Database db, Transaction tr, string name)
        {
            if (string.IsNullOrEmpty(name)) return db.Dimstyle;
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            if (!table.Has(name)) throw new ToolError($"Dimension style '{name}' does not exist.");
            return table[name];
        }

        /// <summary>layer / color / linetype / lineweight, shared by add_entities and update_entities.</summary>
        public static bool ApplyCommon(Entity e, string key, object value, Database db, Transaction tr)
        {
            switch (key.ToLowerInvariant())
            {
                case "layer":
                    e.LayerId = EnsureLayer(db, tr, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return true;
                case "color":
                    e.Color = ParseColor(value);
                    return true;
                case "linetype":
                    string lt = Convert.ToString(value, CultureInfo.InvariantCulture);
                    EnsureLinetype(db, tr, lt);
                    e.Linetype = lt;
                    return true;
                case "lineweight":
                    e.LineWeight = ParseLineWeight(value);
                    return true;
                default:
                    return false;
            }
        }

        public static void Append(BlockTableRecord space, Entity e, Transaction tr)
        {
            space.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }

        static readonly Dictionary<string, short> NamedColors = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = 1, ["yellow"] = 2, ["green"] = 3, ["cyan"] = 4, ["blue"] = 5, ["magenta"] = 6,
            ["white"] = 7, ["black"] = 7, ["gray"] = 8, ["grey"] = 8, ["lightgray"] = 9, ["lightgrey"] = 9,
        };

        /// <summary>ACI number (1-255, 0 = ByBlock, 256 = ByLayer), "ByLayer", "ByBlock", a color name, "#RRGGBB" or [r, g, b].</summary>
        public static Color ParseColor(object value)
        {
            if (value is IList rgb && !(value is string))
            {
                if (rgb.Count != 3) throw new ToolError("An RGB color must be [r, g, b].");
                return Color.FromRgb(Byte(rgb[0]), Byte(rgb[1]), Byte(rgb[2]));
            }
            string s = Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
            if (s.Equals("bylayer", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
            if (s.Equals("byblock", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByBlock, 0);
            if (NamedColors.TryGetValue(s, out short named)) return Color.FromColorIndex(ColorMethod.ByAci, named);
            if (s.StartsWith("#") && s.Length == 7 && int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
                return Color.FromRgb((byte)(hex >> 16), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF));
            if (short.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out short aci))
            {
                if (aci == 256) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
                if (aci == 0) return Color.FromColorIndex(ColorMethod.ByBlock, 0);
                if (aci >= 1 && aci <= 255) return Color.FromColorIndex(ColorMethod.ByAci, aci);
            }
            throw new ToolError($"Unknown color '{s}'. Use an ACI number 1-255, ByLayer, ByBlock, a name (red, yellow...), #RRGGBB or [r, g, b].");
        }

        static byte Byte(object o)
        {
            double v = Args.ToDouble(o, "color");
            if (v < 0 || v > 255) throw new ToolError("RGB components must be 0-255.");
            return (byte)Math.Round(v);
        }

        public static object ColorOf(Color c)
        {
            if (c.IsByLayer) return "ByLayer";
            if (c.IsByBlock) return "ByBlock";
            if (c.IsByAci) return (int)c.ColorIndex;
            return $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
        }

        /// <summary>Lineweight in hundredths of a mm (25 = 0.25 mm), or ByLayer / ByBlock / Default.</summary>
        public static LineWeight ParseLineWeight(object value)
        {
            string s = Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
            if (s.Equals("bylayer", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByLayer;
            if (s.Equals("byblock", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByBlock;
            if (s.Equals("default", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByLineWeightDefault;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && Enum.IsDefined(typeof(LineWeight), n))
                return (LineWeight)n;
            throw new ToolError($"Unknown lineweight '{s}'. Use hundredths of a mm (0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211), ByLayer, ByBlock or Default.");
        }

        public static object LineWeightOf(LineWeight lw)
        {
            switch (lw)
            {
                case LineWeight.ByLayer: return "ByLayer";
                case LineWeight.ByBlock: return "ByBlock";
                case LineWeight.ByLineWeightDefault: return "Default";
                default: return (int)lw;
            }
        }

        /// <summary>Block name as the user sees it (dynamic block references point at anonymous *U blocks).</summary>
        public static string EffectiveName(BlockReference br, Transaction tr)
        {
            try
            {
                if (br.IsDynamicBlock)
                    return ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name;
            }
            catch (AcRx.Exception)
            {
                // fall back to the reference's own block
            }
            return br.Name;
        }

        public static bool IsDefaultJustified(DBText t) =>
            t.HorizontalMode == TextHorizontalMode.TextLeft && t.VerticalMode == TextVerticalMode.TextBase;

        /// <summary>The point the user picked for the text (insertion or alignment point).</summary>
        public static Point3d TextPoint(DBText t) => IsDefaultJustified(t) ? t.Position : t.AlignmentPoint;

        /// <summary>Text a user would search for: TEXT, MTEXT, attribute values, leader text, dimension override.</summary>
        public static string TextOf(Entity e, Transaction tr)
        {
            switch (e)
            {
                case DBText t: return t.TextString;
                case MText m: return m.Text;
                case MLeader ml when ml.ContentType == ContentType.MTextContent: return ml.MText?.Text ?? "";
                case Dimension d: return d.DimensionText;
                case BlockReference br:
                    var parts = new List<string> { EffectiveName(br, tr) };
                    foreach (ObjectId aid in br.AttributeCollection)
                    {
                        if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar) parts.Add(ar.TextString);
                    }
                    return string.Join("\n", parts);
                default: return "";
            }
        }
    }
}
