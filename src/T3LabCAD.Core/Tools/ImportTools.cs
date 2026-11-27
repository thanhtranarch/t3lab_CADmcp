using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
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
                "Copy blocks, layers, text / dimension / multileader / table styles or linetypes from another .dwg or .dwt into the " +
                "drawing, with what they depend on (a block brings its layers and styles; ones that already exist here are kept). " +
                "Objects that already exist are skipped, or redefined with replace=true: a block gets the new contents (every insert " +
                "changes; run sync_block_attributes if its attributes changed); layers and styles get the new settings. Dynamic blocks " +
                "are not redefined here.",
                ObjR("path,kind,names",
                    P("path", Str("Source drawing or template (.dwg / .dwt)")),
                    P("kind", Enum("Kind of object", Kinds)),
                    P("names", Strs("Names to import, wildcards allowed (* = all)")),
                    P("replace", Bool("Redefine objects that already exist (default false: keep them)"))),
                Import, destructive: true, tx: false);
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
            if (c.FilePath != null && string.Equals(path, c.FilePath, StringComparison.OrdinalIgnoreCase))
                throw new ToolError("The source is the drawing itself.");
            List<string> patterns = a.Strs("names");
            if (patterns.Count == 0) throw new ToolError("'names' is empty (use * for everything).");
            bool replace = a.Bool("replace", false);

            Dictionary<string, ObjectId> existing;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // block names used by xrefs / layouts here
            using (var tr = db.TransactionManager.StartTransaction())
            {
                existing = Named(db, tr, kind);
                if (kind == "block")
                    foreach (ObjectId id in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
                    {
                        var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (!existing.ContainsKey(btr.Name)) taken.Add(btr.Name);
                    }
                tr.Commit();
            }

            var imported = new List<object>();
            var replaced = new List<object>();
            var skipped = new List<object>();
            var notFound = new List<object>();
            var conflicts = new List<object>();
            var dynamicNotReplaced = new List<object>();

            using (var source = new Database(false, true))
            {
                source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                source.CloseInput(true);
                var fresh = new ObjectIdCollection();
                var redefine = new List<KeyValuePair<string, ObjectId>>(); // name -> source id
                using (var str = source.TransactionManager.StartTransaction())
                {
                    Dictionary<string, ObjectId> available = Named(source, str, kind);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string pattern in patterns)
                    {
                        var matches = available.Where(kv => Wild.Match(kv.Key, pattern)).ToList();
                        if (matches.Count == 0) { notFound.Add(pattern); continue; }
                        foreach (var kv in matches)
                        {
                            if (!seen.Add(kv.Key)) continue;
                            if (taken.Contains(kv.Key)) { conflicts.Add(kv.Key); continue; }
                            if (!existing.ContainsKey(kv.Key)) { fresh.Add(kv.Value); imported.Add(kv.Key); continue; }
                            if (!replace) { skipped.Add(kv.Key); continue; }
                            if (kind == "block" && (((BlockTableRecord)str.GetObject(kv.Value, OpenMode.ForRead)).IsDynamicBlock ||
                                                    IsDynamic(db, existing[kv.Key])))
                            {
                                dynamicNotReplaced.Add(kv.Key);
                                continue;
                            }
                            redefine.Add(kv);
                            replaced.Add(kv.Key);
                        }
                    }
                    str.Commit();
                }

                // new objects: what they use (layers, styles, nested blocks) comes along, existing ones here are kept
                if (fresh.Count > 0)
                    source.WblockCloneObjects(fresh, OwnerId(db, kind), new IdMapping(), DuplicateRecordCloning.Ignore, false);

                if (redefine.Count > 0)
                {
                    if (kind == "block") foreach (var kv in redefine) RedefineBlock(db, source, existing[kv.Key], kv.Value);
                    else
                        source.WblockCloneObjects(new ObjectIdCollection(redefine.Select(kv => kv.Value).ToArray()), OwnerId(db, kind),
                            new IdMapping(), DuplicateRecordCloning.Replace, false);
                }
                if (fresh.Count > 0 || redefine.Count > 0) c.NeedsRegen = true;
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
            if (conflicts.Count > 0) result["name_used_by_xref_or_layout"] = conflicts;
            if (dynamicNotReplaced.Count > 0)
                result["dynamic_not_replaced"] = dynamicNotReplaced; // their parameters live in the definition: redefine them in AutoCAD
            if (kind == "block" && replaced.Count > 0)
                result["note"] = "Replaced blocks changed every insert. If their attributes changed, run sync_block_attributes.";
            return result;
        }

        static bool IsDynamic(Database db, ObjectId btrId)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                bool dynamic = ((BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead)).IsDynamicBlock;
                tr.Commit();
                return dynamic;
            }
        }

        /// <summary>
        /// Replace only the contents of one block definition: empty it, then copy the source block's entities into it.
        /// Layers, styles and nested blocks the new contents use are added when missing and otherwise left as they are here
        /// (a Replace clone of the whole block would overwrite those too).
        /// </summary>
        static void RedefineBlock(Database db, Database source, ObjectId targetId, ObjectId sourceId)
        {
            var entities = new ObjectIdCollection();
            Point3d origin;
            string comments;
            using (var str = source.TransactionManager.StartTransaction())
            {
                var src = (BlockTableRecord)str.GetObject(sourceId, OpenMode.ForRead);
                foreach (ObjectId id in src) if (!id.IsErased) entities.Add(id);
                origin = src.Origin;
                comments = src.Comments;
                str.Commit();
            }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var target = (BlockTableRecord)tr.GetObject(targetId, OpenMode.ForWrite);
                foreach (ObjectId id in target)
                    if (!id.IsErased) tr.GetObject(id, OpenMode.ForWrite, false, true).Erase(); // also on locked layers
                target.Origin = origin;
                target.Comments = comments ?? "";
                tr.Commit();
            }
            if (entities.Count > 0)
                source.WblockCloneObjects(entities, targetId, new IdMapping(), DuplicateRecordCloning.Ignore, false);
        }
    }
}
