using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>import_from_drawing: copy named objects from another .dwg / .dwt (like DesignCenter drag and drop).</summary>
    internal static class ImportTools
    {
        static readonly string[] Kinds = { "block", "layer", "text_style", "dim_style", "linetype", "mleader_style", "table_style" };

        public static void Register()
        {
            Registry.Add("import_from_drawing", "Import from drawing", ToolKind.Write,
                "Copy blocks, layers, text / dimension / multileader / table styles or linetypes from another .dwg or .dwt into the active " +
                "drawing, with what they depend on (a block brings its layers and styles). Objects that already exist are skipped, " +
                "or redefined with replace=true (every insert of a replaced block changes; run sync_block_attributes if its attributes changed).",
                ObjR("path,kind,names",
                    P("path", Str("Source drawing or template (.dwg / .dwt)")),
                    P("kind", Enum("Kind of object", Kinds)),
                    P("names", Strs("Names to import, wildcards allowed (* = all)")),
                    P("replace", Bool("Redefine objects that already exist (default false: keep them)"))),
                Import, destructive: true);
        }

        static ObjectId OwnerId(Database db, string kind)
        {
            switch (kind)
            {
                case "block": return db.BlockTableId;
                case "layer": return db.LayerTableId;
                case "text_style": return db.TextStyleTableId;
                case "dim_style": return db.DimStyleTableId;
                case "linetype": return db.LinetypeTableId;
                case "mleader_style": return db.MLeaderStyleDictionaryId;
                default: return db.TableStyleDictionaryId;
            }
        }

        static bool Dictionary(string kind) => kind == "mleader_style" || kind == "table_style";

        /// <summary>name -> id of the importable objects of this kind (no layouts, anonymous, xref or xref-dependent records).</summary>
        static Dictionary<string, ObjectId> Named(Database db, Transaction tr, string kind)
        {
            var result = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            if (Dictionary(kind))
            {
                foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(OwnerId(db, kind), OpenMode.ForRead))
                    result[entry.Key] = entry.Value;
                return result;
            }
            foreach (ObjectId id in (SymbolTable)tr.GetObject(OwnerId(db, kind), OpenMode.ForRead))
            {
                var rec = (SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (rec.IsErased || rec.IsDependent || string.IsNullOrEmpty(rec.Name)) continue;
                if (rec is BlockTableRecord btr && (btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference)) continue;
                if (rec is TextStyleTableRecord ts && ts.IsShapeFile) continue;
                result[rec.Name] = id;
            }
            return result;
        }

        static object Import(ToolContext c, Args a)
        {
            var db = c.Db;
            string kind = a.ReqStr("kind").Trim().ToLowerInvariant();
            if (!Kinds.Contains(kind)) throw new ToolError($"kind must be one of: {string.Join(", ", Kinds)}.");
            string path = DocumentTools.ResolvePath(a.ReqStr("path"), ".dwg");
            if (!File.Exists(path)) throw new ToolError($"File not found: {path}");
            if (c.Doc.IsNamedDrawing && string.Equals(path, c.Doc.Name, StringComparison.OrdinalIgnoreCase))
                throw new ToolError("The source is the active drawing itself.");
            List<string> patterns = a.Strs("names");
            if (patterns.Count == 0) throw new ToolError("'names' is empty (use * for everything).");
            bool replace = a.Bool("replace", false);

            Dictionary<string, ObjectId> existing = Named(db, c.Tr, kind);
            var imported = new List<object>();
            var replaced = new List<object>();
            var skipped = new List<object>();
            var notFound = new List<object>();

            using (var source = new Database(false, true))
            {
                source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                source.CloseInput(true);
                var ids = new ObjectIdCollection();
                using (var str = source.TransactionManager.StartTransaction())
                {
                    Dictionary<string, ObjectId> available = Named(source, str, kind);
                    foreach (string pattern in patterns)
                    {
                        var matches = available.Where(kv => Wild.Match(kv.Key, pattern)).ToList();
                        if (matches.Count == 0) { notFound.Add(pattern); continue; }
                        foreach (var kv in matches)
                        {
                            if (ids.Contains(kv.Value)) continue;
                            if (existing.ContainsKey(kv.Key))
                            {
                                if (!replace) { skipped.Add(kv.Key); continue; }
                                replaced.Add(kv.Key);
                            }
                            else imported.Add(kv.Key);
                            ids.Add(kv.Value);
                        }
                    }
                    str.Commit();
                }
                if (ids.Count > 0)
                {
                    source.WblockCloneObjects(ids, OwnerId(db, kind), new IdMapping(),
                        replace ? DuplicateRecordCloning.Replace : DuplicateRecordCloning.Ignore, false);
                    c.NeedsRegen = true;
                }
            }

            var result = new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["from"] = path,
                ["imported"] = imported,
                ["replaced"] = replaced,
                ["skipped_existing"] = skipped,
            };
            if (notFound.Count > 0) result["not_in_source"] = notFound;
            if (kind == "block" && replaced.Count > 0)
                result["note"] = "Replaced blocks changed every insert. If their attributes changed, run sync_block_attributes.";
            return result;
        }
    }
}
