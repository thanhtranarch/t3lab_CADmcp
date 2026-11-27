using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using Microsoft.Win32;
using WinRegistry = Microsoft.Win32.Registry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcRx = Autodesk.AutoCAD.Runtime;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>Text styles, dimension styles, linetypes. List / rename / delete: the named-object tools.</summary>
    internal static class StyleTools
    {
        public static void Register()
        {
            Registry.Add("set_text_style", "Create or edit text style", ToolKind.Write,
                "Create a text style (if missing) and/or change it: font (a TrueType typeface like Arial, a .ttf file, or an .shx " +
                "like romans.shx), bigfont (.shx for Asian text), height (0 = set per text), width_factor, oblique angle, bold / italic " +
                "(TrueType), rename, make current. Texts using the style update. List with list_named_objects kind=text_style.",
                ObjR("name",
                    P("name", Str("Text style name")),
                    P("create", Bool("Create the style when it does not exist (default true; it starts as a copy of the current style)")),
                    P("new_name", Str("Rename the style")),
                    P("font", Str("Arial, arial.ttf, romans.shx...")),
                    P("bigfont", Str("Big font .shx (SHX fonts only), empty to remove")),
                    P("bold", Bool("TrueType bold")),
                    P("italic", Bool("TrueType italic")),
                    P("height", Num("Fixed text height, 0 = variable (default 0 for a new style)")),
                    P("width_factor", Num("Width factor (default 1)")),
                    P("oblique", Num("Oblique angle in degrees, -85 to 85")),
                    P("make_current", Bool("Make it the current text style"))),
                SetTextStyle);

            Registry.Add("set_dim_style", "Create or edit dimension style", ToolKind.Write,
                "Create a dimension style (copy of base, default the current style) and/or change its DIM variables, e.g. " +
                "props {DIMSCALE: 100, DIMTXT: 2.5, DIMASZ: 2.5, DIMEXE: 1.25, DIMEXO: 0.625, DIMGAP: 0.625, DIMDEC: 0, DIMTAD: 1, " +
                "DIMTXSTY: Standard, DIMBLK: _ARCHTICK, DIMCLRD: 1, DIMLUNIT: 2, DIMLFAC: 1}. Dimensions using the style are updated. " +
                "Read all variables with list_named_objects kind=dim_style full=true.",
                ObjR("name",
                    P("name", Str("Dimension style name")),
                    P("create", Bool("Create the style when it does not exist (default true)")),
                    P("base", Str("New style: copy settings from this style (default current)")),
                    P("props", ObjAny("DIM variables to set: name (DIMTXT or Dimtxt) -> value")),
                    P("new_name", Str("Rename the style")),
                    P("make_current", Bool("Make it the current dimension style")),
                    P("update_dimensions", Bool("Recompute dimensions that use the style (default true)"))),
                SetDimStyle, scope: ToolScope.Active);

            Registry.Add("load_linetype", "Load linetypes", ToolKind.Write,
                "Load linetypes from a .lin file (default acadiso.lin, else acad.lin). names accepts wildcards (DASH*, *). " +
                "Without names, lists the linetypes in the file and whether each is loaded.",
                Obj(P("names", Strs("Linetype names, wildcards allowed (default: only list the file)")),
                    P("file", Str("Linetype file name or path (default acadiso.lin)"))),
                LoadLinetype);
        }

        // ---------------------------------------------------------------- text styles

        internal static Dictionary<string, object> TextStyleInfo(TextStyleTableRecord s, bool current)
        {
            var d = new Dictionary<string, object> { ["name"] = s.Name };
            if (current) d["current"] = true;
            FontDescriptor f = s.Font;
            if (!string.IsNullOrEmpty(f.TypeFace))
            {
                d["font"] = f.TypeFace;
                if (f.Bold) d["bold"] = true;
                if (f.Italic) d["italic"] = true;
                if (!string.IsNullOrEmpty(s.FileName)) d["font_file"] = s.FileName;
            }
            else
            {
                d["font"] = s.FileName;
                if (!string.IsNullOrEmpty(s.BigFontFileName)) d["bigfont"] = s.BigFontFileName;
            }
            d["height"] = Geo.R(s.TextSize);
            d["width_factor"] = Geo.R(s.XScale);
            if (Math.Abs(s.ObliquingAngle) > 1e-9) d["oblique"] = Geo.Deg(s.ObliquingAngle);
            if (s.IsVertical) d["vertical"] = true;
            if (s.IsDependent) d["xref"] = true;
            return d;
        }

        static object SetTextStyle(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string name = a.ReqStr("name").Trim();
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            bool created = false;
            ObjectId id;
            if (table.Has(name)) id = table[name];
            else
            {
                if (!a.Bool("create", true)) throw new ToolError($"Text style '{name}' does not exist.");
                Acad.ValidateName(name, "text style");
                var current = (TextStyleTableRecord)tr.GetObject(db.Textstyle, OpenMode.ForRead);
                var rec = new TextStyleTableRecord { Name = name };
                table.UpgradeOpen();
                id = table.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
                rec.FileName = current.FileName;
                rec.BigFontFileName = current.BigFontFileName;
                rec.Font = current.Font;
                rec.XScale = 1;
                rec.TextSize = 0;
                created = true;
            }

            var s = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            if (s.IsDependent) throw new ToolError($"'{s.Name}' comes from an xref and cannot be changed here.");
            var changed = new List<string>();
            if (a.Has("font"))
            {
                SetFont(c, s, a.ReqStr("font").Trim(), a.Bool("bold", false), a.Bool("italic", false));
                changed.Add("font");
            }
            else if (a.Has("bold") || a.Has("italic"))
            {
                FontDescriptor f = s.Font;
                if (string.IsNullOrEmpty(f.TypeFace)) throw new ToolError("bold / italic apply to TrueType fonts only.");
                s.Font = new FontDescriptor(f.TypeFace, a.Bool("bold", f.Bold), a.Bool("italic", f.Italic), f.CharacterSet, f.PitchAndFamily);
                changed.Add("font");
            }
            if (a.Has("bigfont"))
            {
                string big = (a.Str("bigfont") ?? "").Trim();
                if (big.Length > 0 && !string.IsNullOrEmpty(s.Font.TypeFace)) throw new ToolError("A big font goes with an SHX font, not a TrueType one.");
                if (big.Length > 0) FindSupportFile(c, big, FindFileHint.CompiledShapeFile, "Big font");
                s.BigFontFileName = big;
                changed.Add("bigfont");
            }
            if (a.Has("height"))
            {
                double h = a.ReqNum("height");
                if (h < 0) throw new ToolError("height cannot be negative (0 = variable height).");
                s.TextSize = h;
                changed.Add("height");
            }
            if (a.Has("width_factor"))
            {
                double w = a.ReqNum("width_factor");
                if (w <= 0) throw new ToolError("width_factor must be greater than 0.");
                s.XScale = w;
                changed.Add("width_factor");
            }
            if (a.Has("oblique"))
            {
                double o = a.ReqNum("oblique");
                if (Math.Abs(o) > 85) throw new ToolError("oblique must be between -85 and 85 degrees.");
                s.ObliquingAngle = Geo.Rad(o);
                changed.Add("oblique");
            }
            if (a.Has("new_name"))
            {
                string newName = a.ReqStr("new_name").Trim();
                NamedObjectTools.RenameRecord("text_style", table, s, newName);
                changed.Add("name");
            }
            if (a.Bool("make_current", false))
            {
                db.Textstyle = id;
                changed.Add("current");
            }
            if (!created && changed.Count > 0) c.NeedsRegen = true;
            return new Dictionary<string, object>
            {
                ["text_style"] = s.Name,
                ["created"] = created,
                ["changed"] = changed,
                ["style"] = TextStyleInfo(s, db.Textstyle == id),
            };
        }

        static void SetFont(ToolContext c, TextStyleTableRecord s, string font, bool bold, bool italic)
        {
            string ext = Path.GetExtension(font).ToLowerInvariant();
            if (ext == ".shx" || (ext.Length == 0 && FontFileOf(font) == null && TryFindSupportFile(c, font + ".shx", FindFileHint.CompiledShapeFile) != null))
            {
                string file = ext.Length == 0 ? font + ".shx" : font;
                FindSupportFile(c, file, FindFileHint.CompiledShapeFile, "SHX font");
                s.Font = new FontDescriptor("", false, false, 0, 0);
                s.FileName = file;
                return;
            }

            string typeface, fontFile;
            if (ext == ".ttf" || ext == ".ttc" || ext == ".otf")
            {
                string path = FontPath(font) ?? throw new ToolError($"Font file '{font}' is not installed (Windows Fonts folder).");
                fontFile = Path.GetFileName(path);
                typeface = FamilyOf(path) ?? Path.GetFileNameWithoutExtension(font);
            }
            else if (ext.Length == 0)
            {
                typeface = font;
                fontFile = FontFileOf(font) ?? throw new ToolError(
                    $"Font '{font}' was not found: not an installed TrueType typeface and no {font}.shx in the AutoCAD support path.");
            }
            else
            {
                throw new ToolError("font must be a TrueType typeface name (Arial), a .ttf / .ttc / .otf file or an .shx file.");
            }
            s.BigFontFileName = "";
            s.FileName = fontFile;
            s.Font = new FontDescriptor(typeface, bold, italic, 0, 0);
        }

        static string TryFindSupportFile(ToolContext c, string file, FindFileHint hint)
        {
            try { return HostApplicationServices.Current.FindFile(file, c.Db, hint); }
            catch (AcRx.Exception) { return null; }
        }

        static string FindSupportFile(ToolContext c, string file, FindFileHint hint, string what) =>
            TryFindSupportFile(c, file, hint) ?? throw new ToolError($"{what} '{file}' was not found in the AutoCAD support path.");

        static readonly string[] FontKeys = { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts" };

        /// <summary>File of an installed TrueType typeface ("Arial" -> arial.ttf), from the Windows font registry.</summary>
        static string FontFileOf(string typeface)
        {
            foreach (RegistryKey root in new[] { WinRegistry.LocalMachine, WinRegistry.CurrentUser })
            {
                foreach (string keyName in FontKeys)
                {
                    using (RegistryKey key = root.OpenSubKey(keyName))
                    {
                        if (key == null) continue;
                        foreach (string valueName in key.GetValueNames())
                        {
                            int paren = valueName.LastIndexOf(" (", StringComparison.Ordinal);
                            string faces = paren > 0 ? valueName.Substring(0, paren) : valueName;
                            if (!faces.Split('&').Any(f => f.Trim().Equals(typeface, StringComparison.OrdinalIgnoreCase))) continue;
                            if (key.GetValue(valueName) is string file && file.Length > 0) return Path.GetFileName(file);
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>Full path of a font file in the Windows (or per-user) Fonts folder.</summary>
        static string FontPath(string file)
        {
            if (Path.IsPathRooted(file)) return File.Exists(file) ? file : null;
            foreach (string dir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
            })
            {
                string path = Path.Combine(dir, file);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        static string FamilyOf(string path)
        {
            try
            {
                using (var fonts = new System.Drawing.Text.PrivateFontCollection())
                {
                    fonts.AddFontFile(path);
                    return fonts.Families.Length > 0 ? fonts.Families[0].Name : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- dimension styles

        static readonly string[] SummaryDimVars =
        {
            "Dimscale", "Dimtxt", "Dimasz", "Dimexe", "Dimexo", "Dimgap", "Dimdec", "Dimlunit", "Dimtad", "Dimtih", "Dimtoh", "Dimlfac",
            "Dimtxsty", "Dimblk",
        };

        /// <summary>The DIMxxx properties of a dimension style record (Dimtxt, Dimasz...).</summary>
        static IEnumerable<PropertyInfo> DimProperties() =>
            typeof(DimStyleTableRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.StartsWith("Dim", StringComparison.Ordinal) && p.CanRead && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name, StringComparer.Ordinal);

        /// <summary>
        /// Copy every DIM variable (not CopyFrom: that files the whole object, extension dictionary link included).
        /// </summary>
        static void CopyDimVars(DimStyleTableRecord from, DimStyleTableRecord to)
        {
            foreach (PropertyInfo p in DimProperties())
            {
                if (!p.CanWrite) continue;
                try { p.SetValue(to, p.GetValue(from, null), null); }
                catch (Exception) { /* a variable AutoCAD will not take on its own keeps the default */ }
            }
        }

        internal static Dictionary<string, object> DimStyleInfo(DimStyleTableRecord s, bool current, bool full, Transaction tr)
        {
            var d = new Dictionary<string, object> { ["name"] = s.Name };
            if (current) d["current"] = true;
            if (s.IsDependent) d["xref"] = true;
            IEnumerable<PropertyInfo> props = full
                ? DimProperties()
                : SummaryDimVars.Select(n => typeof(DimStyleTableRecord).GetProperty(n)).Where(p => p != null);
            foreach (PropertyInfo p in props)
            {
                try
                {
                    object v = p.GetValue(s, null);
                    d[p.Name.ToUpperInvariant()] = p.Name == "Dimjogang" && v is double rad ? Geo.Deg(rad) : DimValue(v, tr);
                }
                catch (Exception) { /* some variables cannot be read on every style */ }
            }
            return d;
        }

        static object DimValue(object v, Transaction tr)
        {
            switch (v)
            {
                case double dv: return Geo.R(dv);
                case ObjectId id:
                    if (id.IsNull || !id.IsValid || id.IsErased) return "";
                    return tr.GetObject(id, OpenMode.ForRead) is SymbolTableRecord r ? r.Name : id.Handle.ToString();
                case Color col: return Acad.ColorOf(col);
                case LineWeight lw: return Acad.LineWeightOf(lw);
                case bool _:
                case int _:
                case short _:
                case string _:
                    return v;
                case char ch: return ch.ToString();
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        static object SetDimStyle(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string name = a.ReqStr("name").Trim();
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            bool created = false;
            ObjectId id;
            if (table.Has(name)) id = table[name];
            else
            {
                if (!a.Bool("create", true)) throw new ToolError($"Dimension style '{name}' does not exist.");
                Acad.ValidateName(name, "dimension style");
                var source = (DimStyleTableRecord)tr.GetObject(Acad.DimStyle(db, tr, a.Str("base")), OpenMode.ForRead);
                var rec = new DimStyleTableRecord { Name = name };
                table.UpgradeOpen();
                id = table.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
                CopyDimVars(source, rec);
                created = true;
            }

            var s = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            if (s.IsDependent) throw new ToolError($"'{s.Name}' comes from an xref and cannot be changed here.");
            var changed = new List<string>();
            var problems = new List<string>();
            Args props = a.Obj("props");
            foreach (string key in props.Keys.ToList())
            {
                if (props.Raw(key) == null) continue;
                try { changed.Add(SetDimVar(s, key, props.Raw(key), db, tr)); }
                catch (ToolError te) { problems.Add(te.Message); }
            }
            if (problems.Count > 0) throw new ToolError("Nothing was changed. " + string.Join(" ", problems));

            if (a.Has("new_name"))
            {
                NamedObjectTools.RenameRecord("dim_style", table, s, a.ReqStr("new_name").Trim());
                changed.Add("name");
            }
            bool makeCurrent = a.Bool("make_current", false);
            if (makeCurrent) db.Dimstyle = id;
            if (makeCurrent || db.Dimstyle == id) db.SetDimstyleData(s); // header DIM variables follow the current style
            if (makeCurrent) changed.Add("current");

            int updated = 0, skipped = 0;
            if (!created && changed.Count > 0 && a.Bool("update_dimensions", true)) updated = UpdateDimensions(c, id, out skipped);
            var result = new Dictionary<string, object>
            {
                ["dim_style"] = s.Name,
                ["created"] = created,
                ["changed"] = changed,
                ["dimensions_updated"] = updated,
                ["style"] = DimStyleInfo(s, db.Dimstyle == id, false, tr),
            };
            if (skipped > 0) result["dimensions_on_locked_layers"] = skipped;
            return result;
        }

        /// <summary>DIMxxx = value through the record's property of the same name; returns the variable name.</summary>
        static string SetDimVar(DimStyleTableRecord s, string key, object value, Database db, Transaction tr)
        {
            string prop = key.Trim();
            if (!prop.StartsWith("dim", StringComparison.OrdinalIgnoreCase)) prop = "Dim" + prop;
            PropertyInfo pi = typeof(DimStyleTableRecord).GetProperty(prop, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi == null || !pi.CanWrite || pi.GetIndexParameters().Length > 0) throw new ToolError($"Unknown dimension variable '{key}'.");
            string var = pi.Name.ToUpperInvariant();
            Type t = pi.PropertyType;
            object v;
            if (t == typeof(double)) v = var == "DIMJOGANG" ? Geo.Rad(Args.ToDouble(value, var)) : Args.ToDouble(value, var);
            else if (t == typeof(int)) v = (int)Math.Round(Args.ToDouble(value, var));
            else if (t == typeof(short)) v = (short)Math.Round(Args.ToDouble(value, var));
            else if (t == typeof(bool)) v = Args.ToBool(value, var);
            else if (t == typeof(string)) v = Convert.ToString(value, CultureInfo.InvariantCulture);
            else if (t == typeof(char))
            {
                string ch = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (ch.Length != 1) throw new ToolError($"{var} must be one character.");
                v = ch[0];
            }
            else if (t == typeof(Color)) v = Acad.ParseColor(value);
            else if (t == typeof(LineWeight)) v = Acad.ParseLineWeight(value);
            else if (t.IsEnum)
            {
                try
                {
                    v = value is string es && !double.TryParse(es, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        ? System.Enum.Parse(t, es, true)
                        : System.Enum.ToObject(t, (int)Math.Round(Args.ToDouble(value, var)));
                }
                catch (ArgumentException) { throw new ToolError($"{var}: '{value}' is not one of {string.Join(", ", System.Enum.GetNames(t))}."); }
            }
            else if (t == typeof(ObjectId))
            {
                string n = Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
                switch (var)
                {
                    case "DIMTXSTY": v = Acad.TextStyle(db, tr, n.Length == 0 ? "Standard" : n); break;
                    case "DIMBLK":
                    case "DIMBLK1":
                    case "DIMBLK2":
                    case "DIMLDRBLK": v = ArrowId(db, tr, n); break;
                    case "DIMLTYPE":
                    case "DIMLTEX1":
                    case "DIMLTEX2": v = Acad.LinetypeId(db, tr, n.Length == 0 ? "ByBlock" : n); break;
                    default: throw new ToolError($"{var} cannot be set here.");
                }
            }
            else throw new ToolError($"{var} cannot be set here (type {t.Name}).");

            try { pi.SetValue(s, v, null); }
            catch (TargetInvocationException tie) when (tie.InnerException is AcRx.Exception ae)
            {
                throw new ToolError($"AutoCAD refused {var} = {value} ({ae.ErrorStatus}).");
            }
            return var;
        }

        /// <summary>Arrowhead block: "" / ClosedFilled = default; a block in the drawing; or a standard one (_ARCHTICK...) that AutoCAD creates.</summary>
        static ObjectId ArrowId(Database db, Transaction tr, string name)
        {
            if (name.Length == 0 || name == "." || name.Equals("ClosedFilled", StringComparison.OrdinalIgnoreCase)) return ObjectId.Null;
            var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            string standard = "_" + name.TrimStart('_');
            foreach (string candidate in new[] { name, standard })
                if (blocks.Has(candidate)) return blocks[candidate];

            // Setting DIMBLK makes AutoCAD add the standard arrowhead block; the old value is put back.
            string old = Convert.ToString(AcApp.GetSystemVariable("DIMBLK"), CultureInfo.InvariantCulture);
            try { AcApp.SetSystemVariable("DIMBLK", standard); }
            catch (Exception)
            {
                throw new ToolError($"Unknown arrowhead '{name}'. Use a block in the drawing or a standard one: _ARCHTICK, _OBLIQUE, _DOT, " +
                                    "_DOTSMALL, _DOTBLANK, _OPEN, _OPEN30, _OPEN90, _CLOSED, _CLOSEDBLANK, _SMALL, _NONE, _INTEGRAL, _DATUMFILLED, _BOXFILLED.");
            }
            finally
            {
                try { AcApp.SetSystemVariable("DIMBLK", old.Length == 0 ? "." : old); }
                catch (Exception) { /* keep going: only the header variable is affected */ }
            }
            blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (blocks.Has(standard)) return blocks[standard];
            throw new ToolError($"Arrowhead '{name}' could not be created.");
        }

        static int UpdateDimensions(ToolContext c, ObjectId styleId, out int lockedSkipped)
        {
            int updated = 0;
            lockedSkipped = 0;
            var layouts = (DBDictionary)c.Tr.GetObject(c.Db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                var layout = (Layout)c.Tr.GetObject(entry.Value, OpenMode.ForRead);
                foreach (ObjectId id in (BlockTableRecord)c.Tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead))
                {
                    if (!id.ObjectClass.DxfName.EndsWith("DIMENSION", StringComparison.Ordinal)) continue;
                    if (!(c.Tr.GetObject(id, OpenMode.ForRead) is Dimension dim) || dim.DimensionStyle != styleId) continue;
                    try
                    {
                        dim.UpgradeOpen();
                        dim.RecomputeDimensionBlock(true);
                        updated++;
                    }
                    catch (AcRx.Exception ae) when (ae.ErrorStatus == AcRx.ErrorStatus.OnLockedLayer)
                    {
                        lockedSkipped++;
                    }
                }
            }
            return updated;
        }

        // ---------------------------------------------------------------- linetypes

        internal static Dictionary<string, object> LinetypeInfo(LinetypeTableRecord lt, bool current)
        {
            var d = new Dictionary<string, object> { ["name"] = lt.Name };
            if (current) d["current"] = true;
            if (!string.IsNullOrEmpty(lt.AsciiDescription)) d["description"] = lt.AsciiDescription;
            if (lt.NumDashes > 0)
            {
                d["dashes"] = lt.NumDashes;
                d["pattern_length"] = Geo.R(lt.PatternLength);
            }
            if (lt.IsDependent) d["xref"] = true;
            return d;
        }

        static object LoadLinetype(ToolContext c, Args a)
        {
            var db = c.Db;
            string file = a.Str("file");
            string path = null;
            foreach (string candidate in file != null ? new[] { file } : new[] { "acadiso.lin", "acad.lin" })
            {
                path = Path.IsPathRooted(candidate) && File.Exists(candidate) ? candidate : TryFindSupportFile(c, candidate, FindFileHint.Default);
                if (path != null) break;
            }
            if (path == null) throw new ToolError($"Linetype file '{file ?? "acadiso.lin"}' was not found in the AutoCAD support path.");

            var available = new List<KeyValuePair<string, string>>();
            foreach (string line in File.ReadAllLines(path))
            {
                string t = line.Trim();
                if (!t.StartsWith("*", StringComparison.Ordinal)) continue;
                int comma = t.IndexOf(',');
                string ltName = (comma > 0 ? t.Substring(1, comma - 1) : t.Substring(1)).Trim();
                if (ltName.Length > 0) available.Add(new KeyValuePair<string, string>(ltName, comma > 0 ? t.Substring(comma + 1).Trim() : ""));
            }

            var table = (LinetypeTable)c.Tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            List<string> names = a.Strs("names");
            if (names.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    ["file"] = path,
                    ["count"] = available.Count,
                    ["linetypes"] = available.Select(kv => (object)new Dictionary<string, object>
                    {
                        ["name"] = kv.Key,
                        ["description"] = kv.Value,
                        ["loaded"] = table.Has(kv.Key),
                    }).ToList(),
                };
            }

            var loaded = new List<object>();
            var already = new List<object>();
            var notFound = new List<object>();
            foreach (string pattern in names)
            {
                var matches = available.Where(kv => Wild.Match(kv.Key, pattern)).Select(kv => kv.Key).ToList();
                if (matches.Count == 0) { notFound.Add(pattern); continue; }
                foreach (string ltName in matches)
                {
                    table = (LinetypeTable)c.Tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                    if (table.Has(ltName)) { if (!already.Contains(ltName)) already.Add(ltName); continue; }
                    db.LoadLineTypeFile(ltName, path);
                    loaded.Add(ltName);
                }
            }
            var result = new Dictionary<string, object> { ["file"] = path, ["loaded"] = loaded, ["already_loaded"] = already };
            if (notFound.Count > 0) result["not_in_file"] = notFound;
            return result;
        }
    }
}
