using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using AcRx = Autodesk.AutoCAD.Runtime;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>
    /// One set of tools for every kind of named object: list, rename, delete, purge, plus groups.
    /// Kinds: layer, block, layout, text_style, dim_style, linetype, view, ucs, group, mleader_style, table_style, xref, regapp.
    /// </summary>
    internal static class NamedObjectTools
    {
        static readonly string[] ListKinds =
            { "layer", "block", "layout", "text_style", "dim_style", "linetype", "view", "ucs", "group", "mleader_style", "table_style", "page_setup", "xref" };
        static readonly string[] EditKinds =
            { "layer", "block", "layout", "text_style", "dim_style", "linetype", "view", "ucs", "group", "mleader_style", "table_style", "page_setup" };
        static readonly string[] PurgeKinds =
            { "block", "layer", "linetype", "text_style", "dim_style", "mleader_style", "table_style", "regapp" };

        public static void Register()
        {
            Registry.Add("list_named_objects", "List named objects", ToolKind.Read,
                "Named objects of the active drawing by kind: text_style (font, height, width factor), dim_style (main DIM variables, " +
                "full=true for all), linetype, view, ucs, group (entity handles), mleader_style, table_style, xref (path and status: " +
                "loaded, unloaded, not_found...), page_setup (plotter, paper, plot style, scale). layer, block and layout return the same as " +
                "list_layers, list_blocks and list_layouts.",
                ObjR("kind",
                    P("kind", Enum("Kind of object", ListKinds)),
                    P("name", Str("Wildcard filter, e.g. A-*")),
                    P("full", Bool("dim_style: every DIM variable; group: every handle (default false)")),
                    P("include_xref", Bool("Include xref-dependent styles / linetypes (NAME|...) (default false)")),
                    P("include_anonymous", Bool("group: include unnamed groups (default false)"))),
                ListNamed);

            Registry.Add("rename_object", "Rename named object", ToolKind.Write,
                "Rename a layer, block, layout, text style, dimension style, linetype, named view, UCS, group, multileader style, table style or page setup. " +
                "Layer 0 / Defpoints, Model, Standard styles, ByLayer / ByBlock / Continuous and xref-dependent names (XREF|NAME) cannot be renamed.",
                ObjR("kind,name,new_name",
                    P("kind", Enum("Kind of object", EditKinds)),
                    P("name", Str("Current name")),
                    P("new_name", Str("New name"))),
                Rename);

            Registry.Add("delete_object", "Delete named object", ToolKind.Write,
                "Delete named objects of one kind; each name succeeds or fails on its own (results say why). Objects still in use are refused: " +
                "layer: merge_into moves its entities to another layer first; block: erase_references=true also erases its inserts; " +
                "layout: Model and the last paper layout stay; group: ungroups (the entities stay). Layer 0, Standard styles and current " +
                "objects cannot be deleted. Xrefs: manage_xref detach. Unused objects in bulk: purge.",
                ObjR("kind,names",
                    P("kind", Enum("Kind of object", EditKinds)),
                    P("names", Strs("Exact names to delete (no wildcards)")),
                    P("merge_into", Str("layer: layer that receives the entities")),
                    P("erase_references", Bool("block: also erase every insert of the block (default false)"))),
                Delete, destructive: true);

            Registry.Add("purge", "Purge unused", ToolKind.Write,
                "Remove unused named objects like PURGE: blocks, layers, linetypes, text / dimension / multileader / table styles and " +
                "registered applications (regapp). Repeats for nested items (a purged block can free a layer). dry_run=true only reports.",
                Obj(P("kinds", EnumArr("Kinds to purge (default all)", PurgeKinds)),
                    P("names", Strs("Only these names, wildcards allowed (default all)")),
                    P("dry_run", Bool("Report what would be purged, change nothing (default false)"))),
                Purge, destructive: true);

            Registry.Add("set_group", "Create or edit group", ToolKind.Write,
                "Create a named group of entities (if missing) and/or edit it: add handles, remove_handles, description, selectable. " +
                "List with list_named_objects kind=group; ungroup with delete_object kind=group (entities stay).",
                ObjR("name",
                    P("name", Str("Group name")),
                    P("create", Bool("Create the group when it does not exist (default true)")),
                    Handles,
                    P("remove_handles", Strs("Entities to take out of the group")),
                    P("description", Str("Group description")),
                    P("selectable", Bool("Selecting one member selects the whole group (default true)"))),
                SetGroup);
        }

        // ---------------------------------------------------------------- helpers

        static string KindArg(Args a, string[] allowed)
        {
            string kind = (a.ReqStr("kind")).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            if (kind == "textstyle" || kind == "style") kind = "text_style";
            if (kind == "dimstyle") kind = "dim_style";
            if (kind == "mleaderstyle" || kind == "multileader_style") kind = "mleader_style";
            if (kind == "tablestyle") kind = "table_style";
            if (!allowed.Contains(kind)) throw new ToolError($"kind must be one of: {string.Join(", ", allowed)}.");
            return kind;
        }

        static string Label(string kind)
        {
            switch (kind)
            {
                case "text_style": return "text style";
                case "dim_style": return "dimension style";
                case "mleader_style": return "multileader style";
                case "page_setup": return "page setup";
                case "table_style": return "table style";
                case "ucs": return "UCS";
                case "regapp": return "registered application";
                default: return kind;
            }
        }

        static ObjectId TableId(Database db, string kind)
        {
            switch (kind)
            {
                case "layer": return db.LayerTableId;
                case "block": return db.BlockTableId;
                case "text_style": return db.TextStyleTableId;
                case "dim_style": return db.DimStyleTableId;
                case "linetype": return db.LinetypeTableId;
                case "view": return db.ViewTableId;
                case "ucs": return db.UcsTableId;
                case "regapp": return db.RegAppTableId;
                default: return ObjectId.Null;
            }
        }

        static ObjectId DictId(Database db, string kind)
        {
            switch (kind)
            {
                case "group": return db.GroupDictionaryId;
                case "mleader_style": return db.MLeaderStyleDictionaryId;
                case "table_style": return db.TableStyleDictionaryId;
                case "page_setup": return db.PlotSettingsDictionaryId;
                default: return ObjectId.Null;
            }
        }

        static bool IsStyleKind(string kind) =>
            kind == "text_style" || kind == "dim_style" || kind == "mleader_style" || kind == "table_style";

        static bool IsSpecialLinetype(string name) =>
            name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) || name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Continuous", StringComparison.OrdinalIgnoreCase);

        /// <summary>Why AutoCAD's own objects of this kind cannot be renamed or deleted (null = allowed).</summary>
        static string Protected(string kind, string name)
        {
            if (kind == "layer" && (name == "0" || name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)))
                return $"Layer '{name}' belongs to AutoCAD and cannot be renamed or deleted.";
            if (kind == "linetype" && IsSpecialLinetype(name))
                return $"Linetype '{name}' belongs to AutoCAD and cannot be renamed or deleted.";
            if (IsStyleKind(kind) && name.Equals("Standard", StringComparison.OrdinalIgnoreCase))
                return $"The Standard {Label(kind)} cannot be renamed or deleted.";
            if (kind == "layout" && name.Equals("Model", StringComparison.OrdinalIgnoreCase))
                return "The Model layout cannot be renamed or deleted.";
            return null;
        }

        static ToolError NotFound(string kind, string name) =>
            new ToolError($"No {Label(kind)} named '{name}' (see list_named_objects kind={kind}).");

        static string Message(AcRx.Exception ae) =>
            ae.ErrorStatus == AcRx.ErrorStatus.OnLockedLayer ? "an entity involved is on a locked layer" : "AutoCAD error " + ae.ErrorStatus;

        /// <summary>Rename a symbol table record with the usual checks (also used by set_text_style / set_dim_style).</summary>
        internal static void RenameRecord(string kind, SymbolTable table, SymbolTableRecord rec, string newName)
        {
            string why = Protected(kind, rec.Name);
            if (why != null) throw new ToolError(why);
            if (rec.IsDependent) throw new ToolError($"'{rec.Name}' comes from an xref: rename it in the xref drawing.");
            if (kind == "block")
            {
                var btr = (BlockTableRecord)rec;
                if (btr.IsLayout) throw new ToolError($"'{rec.Name}' is a layout block: rename the layout instead (kind=layout).");
                if (btr.IsAnonymous) throw new ToolError($"'{rec.Name}' is an anonymous block and cannot be renamed.");
                if (btr.IsFromExternalReference)
                    throw new ToolError($"'{rec.Name}' is an xref: its layers and styles keep the {rec.Name}| prefix, so it is not renamed here.");
            }
            if (rec.Name == newName) throw new ToolError("new_name is the current name.");
            Acad.ValidateName(newName, Label(kind));
            if (table.Has(newName) && table[newName] != rec.ObjectId) throw new ToolError($"A {Label(kind)} named '{newName}' already exists.");
            if (!rec.IsWriteEnabled) rec.UpgradeOpen();
            rec.Name = newName;
        }

        /// <summary>Throws when something still references the object (Database.Purge keeps only unreferenced ids).</summary>
        static void EnsureUnused(Database db, ObjectId id, string what)
        {
            var ids = new ObjectIdCollection { id };
            db.Purge(ids);
            if (ids.Count == 0) throw new ToolError($"{what} is still in use (by entities, other styles or block definitions).");
        }

        static int PaperLayoutCount(Transaction tr, Database db)
        {
            int n = 0;
            foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead))
                if (!((Layout)tr.GetObject(entry.Value, OpenMode.ForRead)).ModelType) n++;
            return n;
        }

        // ---------------------------------------------------------------- list

        static object ListNamed(ToolContext c, Args a)
        {
            string kind = KindArg(a, ListKinds);
            switch (kind)
            {
                case "layer": return LayerTools.ListLayers(c, a);
                case "block": return QueryTools.ListBlocks(c, a);
                case "layout": return DocumentTools.ListLayouts(c, a);
            }

            var db = c.Db;
            var tr = c.Tr;
            string filter = a.Str("name");
            bool full = a.Bool("full", false), xrefs = a.Bool("include_xref", false);
            var items = new List<object>();
            bool Keep(string name, bool dependent) => (xrefs || !dependent) && (filter == null || Wild.Match(name, filter));

            switch (kind)
            {
                case "text_style":
                    foreach (ObjectId id in (SymbolTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead))
                    {
                        var s = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (s.IsErased || s.IsShapeFile || string.IsNullOrEmpty(s.Name) || !Keep(s.Name, s.IsDependent)) continue;
                        items.Add(StyleTools.TextStyleInfo(s, id == db.Textstyle));
                    }
                    break;

                case "dim_style":
                    foreach (ObjectId id in (SymbolTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead))
                    {
                        var s = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (s.IsErased || !Keep(s.Name, s.IsDependent)) continue;
                        items.Add(StyleTools.DimStyleInfo(s, id == db.Dimstyle, full, tr));
                    }
                    break;

                case "linetype":
                    foreach (ObjectId id in (SymbolTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead))
                    {
                        var lt = (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (lt.IsErased || !Keep(lt.Name, lt.IsDependent)) continue;
                        items.Add(StyleTools.LinetypeInfo(lt, id == db.Celtype));
                    }
                    break;

                case "view":
                    foreach (ObjectId id in (SymbolTable)tr.GetObject(db.ViewTableId, OpenMode.ForRead))
                    {
                        var v = (ViewTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (v.IsErased || !Keep(v.Name, v.IsDependent)) continue;
                        var d = new Dictionary<string, object>
                        {
                            ["name"] = v.Name,
                            ["space"] = v.IsPaperspaceView ? "paper" : "model",
                            ["center"] = Geo.P(v.CenterPoint),
                            ["width"] = Geo.R(v.Width),
                            ["height"] = Geo.R(v.Height),
                        };
                        if (!v.ViewDirection.IsParallelTo(Autodesk.AutoCAD.Geometry.Vector3d.ZAxis)) d["direction"] = Geo.V(v.ViewDirection);
                        if (!string.IsNullOrEmpty(v.CategoryName)) d["category"] = v.CategoryName;
                        items.Add(d);
                    }
                    break;

                case "ucs":
                    foreach (ObjectId id in (SymbolTable)tr.GetObject(db.UcsTableId, OpenMode.ForRead))
                    {
                        var u = (UcsTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (u.IsErased || !Keep(u.Name, u.IsDependent)) continue;
                        items.Add(new Dictionary<string, object>
                        {
                            ["name"] = u.Name,
                            ["origin"] = Geo.P(u.Origin),
                            ["x_axis"] = Geo.V(u.XAxis),
                            ["y_axis"] = Geo.V(u.YAxis),
                        });
                    }
                    break;

                case "group":
                    bool anonymous = a.Bool("include_anonymous", false);
                    foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead))
                    {
                        if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Group g) || g.IsErased) continue;
                        if (g.IsAnonymous && !anonymous) continue;
                        if (filter != null && !Wild.Match(entry.Key, filter)) continue;
                        ObjectId[] ids = g.GetAllEntityIds();
                        var d = new Dictionary<string, object>
                        {
                            ["name"] = entry.Key,
                            ["entities"] = ids.Length,
                            ["selectable"] = g.Selectable,
                            ["handles"] = ids.Take(full ? ids.Length : 100).Select(i => (object)i.Handle.ToString()).ToList(),
                        };
                        if (!string.IsNullOrEmpty(g.Description)) d["description"] = g.Description;
                        if (g.IsAnonymous) d["anonymous"] = true;
                        items.Add(d);
                    }
                    break;

                case "mleader_style":
                    foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(db.MLeaderStyleDictionaryId, OpenMode.ForRead))
                    {
                        if (filter != null && !Wild.Match(entry.Key, filter)) continue;
                        if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is MLeaderStyle s)) continue;
                        var d = new Dictionary<string, object>
                        {
                            ["name"] = entry.Key,
                            ["text_height"] = Geo.R(s.TextHeight),
                            ["arrow_size"] = Geo.R(s.ArrowSize),
                            ["scale"] = Geo.R(s.Scale),
                        };
                        if (entry.Value == db.MLeaderstyle) d["current"] = true;
                        items.Add(d);
                    }
                    break;

                case "table_style":
                    foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead))
                    {
                        if (filter != null && !Wild.Match(entry.Key, filter)) continue;
                        if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is TableStyle s)) continue;
                        var d = new Dictionary<string, object> { ["name"] = entry.Key };
                        if (entry.Value == db.Tablestyle) d["current"] = true;
                        if (!string.IsNullOrEmpty(s.Description)) d["description"] = s.Description;
                        items.Add(d);
                    }
                    break;

                case "page_setup":
                    foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead))
                    {
                        if (filter != null && !Wild.Match(entry.Key, filter)) continue;
                        if (tr.GetObject(entry.Value, OpenMode.ForRead) is PlotSettings ps) items.Add(SheetTools.PageSetupInfo(ps));
                    }
                    break;

                case "xref":
                    foreach (ObjectId id in (SymbolTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
                    {
                        var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (btr.IsErased || !btr.IsFromExternalReference) continue;
                        if (filter != null && !Wild.Match(btr.Name, filter)) continue;
                        items.Add(XrefTools.Info(btr));
                    }
                    break;
            }
            return new Dictionary<string, object> { ["kind"] = kind, ["count"] = items.Count, ["items"] = items };
        }

        // ---------------------------------------------------------------- rename

        static object Rename(ToolContext c, Args a)
        {
            string kind = KindArg(a, EditKinds);
            string name = a.ReqStr("name").Trim(), newName = a.ReqStr("new_name").Trim();
            var db = c.Db;
            var tr = c.Tr;

            string why = Protected(kind, name);
            if (why != null) throw new ToolError(why);

            if (kind == "layout")
            {
                LayoutManager lm = LayoutManager.Current;
                if (!lm.LayoutExists(name)) throw NotFound(kind, name);
                Acad.ValidateName(newName, "layout");
                if (newName.Equals("Model", StringComparison.OrdinalIgnoreCase) ||
                    (lm.LayoutExists(newName) && !newName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new ToolError($"A layout named '{newName}' already exists.");
                lm.RenameLayout(name, newName);
            }
            else if (DictId(db, kind) != ObjectId.Null)
            {
                var dict = (DBDictionary)tr.GetObject(DictId(db, kind), OpenMode.ForRead);
                if (!dict.Contains(name)) throw NotFound(kind, name);
                ObjectId id = dict.GetAt(name);
                Acad.ValidateName(newName, Label(kind));
                if (dict.Contains(newName) && dict.GetAt(newName) != id) throw new ToolError($"A {Label(kind)} named '{newName}' already exists.");
                dict.UpgradeOpen();
                dict.SetName(name, newName);
                DBObject o = tr.GetObject(id, OpenMode.ForWrite);
                // styles also keep their name in the object itself
                try
                {
                    if (o is MLeaderStyle ms) ms.Name = newName;
                    else if (o is TableStyle ts) ts.Name = newName;
                    else if (o is PlotSettings ps) ps.PlotSettingsName = newName;
                }
                catch (AcRx.Exception) { /* the dictionary key is what AutoCAD shows */ }
            }
            else
            {
                var table = (SymbolTable)tr.GetObject(TableId(db, kind), OpenMode.ForRead);
                if (!table.Has(name)) throw NotFound(kind, name);
                RenameRecord(kind, table, (SymbolTableRecord)tr.GetObject(table[name], OpenMode.ForWrite), newName);
            }
            if (kind == "block" || kind == "text_style") c.NeedsRegen = true;
            return new Dictionary<string, object> { ["kind"] = kind, ["renamed"] = name, ["to"] = newName };
        }

        // ---------------------------------------------------------------- delete

        static object Delete(ToolContext c, Args a)
        {
            string kind = KindArg(a, EditKinds);
            List<string> names = a.Strs("names").Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0) throw new ToolError("'names' is empty.");

            var results = new List<object>();
            int deleted = 0;
            string lastError = null;
            foreach (string name in names)
            {
                var r = new Dictionary<string, object> { ["name"] = name };
                try
                {
                    // one nested transaction per name: a failure undoes only that name
                    using (var sub = c.Db.TransactionManager.StartTransaction())
                    {
                        var sc = new ToolContext { Doc = c.Doc, Db = c.Db, Ed = c.Ed, Tr = sub };
                        Dictionary<string, object> details = DeleteOne(sc, kind, name, a);
                        sub.Commit();
                        if (details != null)
                            foreach (var kv in details)
                                if (kv.Key != "deleted") r[kv.Key] = kv.Value;
                    }
                    r["ok"] = true;
                    deleted++;
                }
                catch (ToolError te) { r["ok"] = false; r["error"] = lastError = te.Message; }
                catch (AcRx.Exception ae) { r["ok"] = false; r["error"] = lastError = Message(ae); }
                results.Add(r);
            }
            if (names.Count == 1 && deleted == 0) throw new ToolError(lastError);
            return new Dictionary<string, object> { ["kind"] = kind, ["deleted"] = deleted, ["results"] = results };
        }

        static Dictionary<string, object> DeleteOne(ToolContext c, string kind, string name, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string why = Protected(kind, name);
            if (why != null) throw new ToolError(why);

            switch (kind)
            {
                case "layer":
                    return LayerTools.DeleteLayerCore(c, name, a.Str("merge_into"));

                case "layout":
                {
                    LayoutManager lm = LayoutManager.Current;
                    if (!lm.LayoutExists(name)) throw NotFound(kind, name);
                    if (PaperLayoutCount(tr, db) <= 1) throw new ToolError("A drawing keeps at least one paper space layout.");
                    if (lm.CurrentLayout.Equals(name, StringComparison.OrdinalIgnoreCase)) lm.CurrentLayout = "Model";
                    lm.DeleteLayout(name);
                    return null;
                }

                case "block":
                    return DeleteBlock(c, name, a.Bool("erase_references", false));

                case "group":
                {
                    var dict = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead);
                    if (!dict.Contains(name)) throw NotFound(kind, name);
                    var g = (Group)tr.GetObject(dict.GetAt(name), OpenMode.ForWrite);
                    int members = g.NumEntities;
                    g.Erase();
                    return new Dictionary<string, object> { ["entities_kept"] = members };
                }

                case "page_setup":
                {
                    // layouts copy a page setup's values, they do not point at it: erasing one changes no layout
                    var dict = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
                    if (!dict.Contains(name)) throw NotFound(kind, name);
                    tr.GetObject(dict.GetAt(name), OpenMode.ForWrite).Erase();
                    return null;
                }

                case "mleader_style":
                case "table_style":
                {
                    var dict = (DBDictionary)tr.GetObject(DictId(db, kind), OpenMode.ForRead);
                    if (!dict.Contains(name)) throw NotFound(kind, name);
                    ObjectId id = dict.GetAt(name);
                    if (id == (kind == "mleader_style" ? db.MLeaderstyle : db.Tablestyle))
                        throw new ToolError($"'{name}' is the current {Label(kind)}: make another one current first.");
                    EnsureUnused(db, id, $"The {Label(kind)} '{name}'");
                    tr.GetObject(id, OpenMode.ForWrite).Erase();
                    return null;
                }

                default: // text_style, dim_style, linetype, view, ucs
                {
                    var table = (SymbolTable)tr.GetObject(TableId(db, kind), OpenMode.ForRead);
                    if (!table.Has(name)) throw NotFound(kind, name);
                    ObjectId id = table[name];
                    var rec = (SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (rec.IsDependent) throw new ToolError($"'{name}' comes from an xref (manage_xref detaches the xref).");
                    if ((kind == "text_style" && id == db.Textstyle) || (kind == "dim_style" && id == db.Dimstyle) ||
                        (kind == "linetype" && id == db.Celtype))
                        throw new ToolError($"'{name}' is the current {Label(kind)}: make another one current first.");
                    EnsureUnused(db, id, $"The {Label(kind)} '{name}'");
                    rec.UpgradeOpen();
                    rec.Erase();
                    return null;
                }
            }
        }

        static Dictionary<string, object> DeleteBlock(ToolContext c, string name, bool eraseReferences)
        {
            var db = c.Db;
            var tr = c.Tr;
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!table.Has(name)) throw NotFound("block", name);
            var btr = (BlockTableRecord)tr.GetObject(table[name], OpenMode.ForRead);
            if (btr.IsLayout) throw new ToolError($"'{name}' is a layout block: delete the layout instead (kind=layout).");
            if (btr.IsFromExternalReference) throw new ToolError($"'{name}' is an xref: use manage_xref action=detach.");
            if (btr.IsDependent) throw new ToolError($"'{name}' comes from an xref.");

            var definitions = new List<ObjectId> { btr.ObjectId };
            if (btr.IsDynamicBlock) definitions.AddRange(btr.GetAnonymousBlockIds().Cast<ObjectId>());
            var references = new List<ObjectId>();
            foreach (ObjectId defId in definitions)
                foreach (ObjectId refId in ((BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead)).GetBlockReferenceIds(true, false))
                    if (!refId.IsErased && !references.Contains(refId)) references.Add(refId);

            if (references.Count > 0 && !eraseReferences)
                throw new ToolError($"Block '{name}' has {references.Count} insert(s). Pass erase_references=true to erase them as well.");
            foreach (ObjectId refId in references)
            {
                try { tr.GetObject(refId, OpenMode.ForWrite).Erase(); }
                catch (AcRx.Exception ae) when (ae.ErrorStatus == AcRx.ErrorStatus.OnLockedLayer)
                {
                    throw new ToolError($"An insert of '{name}' ({refId.Handle}) is on a locked layer. Unlock it first (set_layer locked=false).");
                }
            }

            var ids = new ObjectIdCollection(definitions.ToArray());
            db.Purge(ids);
            if (!ids.Contains(btr.ObjectId))
                throw new ToolError($"Block '{name}' is still used (nested in another block, or by a dimension / leader style). Nothing was erased.");
            foreach (ObjectId id in ids) tr.GetObject(id, OpenMode.ForWrite).Erase();
            return new Dictionary<string, object> { ["inserts_erased"] = references.Count };
        }

        // ---------------------------------------------------------------- purge

        static object Purge(ToolContext c, Args a)
        {
            var db = c.Db;
            List<string> kinds = a.Has("kinds")
                ? a.Strs("kinds").Select(k => k.Trim().ToLowerInvariant()).Distinct().ToList()
                : PurgeKinds.ToList();
            foreach (string k in kinds)
                if (!PurgeKinds.Contains(k)) throw new ToolError($"Cannot purge '{k}'. Kinds: {string.Join(", ", PurgeKinds)}.");
            List<string> names = a.Strs("names");
            bool dryRun = a.Bool("dry_run", false);

            var purged = kinds.ToDictionary(k => k, k => new List<string>());
            int passes = 0;
            using (var sub = db.TransactionManager.StartTransaction())
            {
                while (passes < 20)
                {
                    var candidates = new Dictionary<ObjectId, KeyValuePair<string, string>>();
                    foreach (string kind in kinds) Candidates(sub, db, kind, names, candidates);
                    if (candidates.Count == 0) break;
                    var ids = new ObjectIdCollection(candidates.Keys.ToArray());
                    db.Purge(ids);
                    if (ids.Count == 0) break;
                    passes++;
                    foreach (ObjectId id in ids)
                    {
                        KeyValuePair<string, string> what = candidates[id];
                        purged[what.Key].Add(what.Value);
                        sub.GetObject(id, OpenMode.ForWrite).Erase();
                    }
                }
                // a dry run erases inside this transaction to find nested items, then rolls everything back
                if (dryRun) sub.Abort();
                else sub.Commit();
            }

            var report = new Dictionary<string, object>();
            int total = 0;
            foreach (var kv in purged)
            {
                if (kv.Value.Count == 0) continue;
                total += kv.Value.Count;
                var list = kv.Value.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(300).Cast<object>().ToList();
                report[kv.Key] = kv.Value.Count > list.Count
                    ? new Dictionary<string, object> { ["count"] = kv.Value.Count, ["first"] = list }
                    : (object)list;
            }
            return new Dictionary<string, object>
            {
                ["dry_run"] = dryRun,
                [dryRun ? "would_purge" : "purged"] = report,
                ["total"] = total,
                ["passes"] = passes,
            };
        }

        static void Candidates(Transaction tr, Database db, string kind, List<string> names, Dictionary<ObjectId, KeyValuePair<string, string>> into)
        {
            void Add(ObjectId id, string name)
            {
                if (names.Count > 0 && !Wild.Any(name, names)) return;
                into[id] = new KeyValuePair<string, string>(kind, name);
            }

            if (kind == "mleader_style" || kind == "table_style")
            {
                ObjectId current = kind == "mleader_style" ? db.MLeaderstyle : db.Tablestyle;
                foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(DictId(db, kind), OpenMode.ForRead))
                {
                    if (entry.Value == current || entry.Value.IsErased || Protected(kind, entry.Key) != null) continue;
                    Add(entry.Value, entry.Key);
                }
                return;
            }

            foreach (ObjectId id in (SymbolTable)tr.GetObject(TableId(db, kind), OpenMode.ForRead))
            {
                if (id.IsErased) continue;
                var rec = (SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (rec.IsDependent || string.IsNullOrEmpty(rec.Name) || Protected(kind, rec.Name) != null) continue;
                switch (kind)
                {
                    case "layer":
                        if (id == db.Clayer) continue;
                        break;
                    case "linetype":
                        if (id == db.Celtype) continue;
                        break;
                    case "text_style":
                        if (id == db.Textstyle || ((TextStyleTableRecord)rec).IsShapeFile) continue;
                        break;
                    case "dim_style":
                        if (id == db.Dimstyle) continue;
                        break;
                    case "regapp":
                        if (rec.Name.Equals("ACAD", StringComparison.OrdinalIgnoreCase)) continue;
                        break;
                    case "block":
                        var btr = (BlockTableRecord)rec;
                        if (btr.IsLayout || btr.IsFromExternalReference) continue;
                        // a dynamic block whose anonymous copies are inserted is in use even if its own record is not
                        if (btr.IsDynamicBlock && btr.GetAnonymousBlockIds().Cast<ObjectId>()
                                .Any(anon => ((BlockTableRecord)tr.GetObject(anon, OpenMode.ForRead)).GetBlockReferenceIds(true, false).Count > 0))
                            continue;
                        break;
                }
                Add(id, rec.Name);
            }
        }

        // ---------------------------------------------------------------- groups

        static object SetGroup(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string name = a.ReqStr("name").Trim();
            var dict = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead);
            bool created = false;
            Group g;
            if (dict.Contains(name)) g = (Group)tr.GetObject(dict.GetAt(name), OpenMode.ForWrite);
            else
            {
                if (!a.Bool("create", true)) throw NotFound("group", name);
                Acad.ValidateName(name, "group");
                g = new Group(a.Str("description") ?? "", a.Bool("selectable", true));
                dict.UpgradeOpen();
                dict.SetAt(name, g);
                tr.AddNewlyCreatedDBObject(g, true);
                created = true;
            }

            if (!created && a.Has("description")) g.Description = a.Str("description") ?? "";
            if (!created && a.Has("selectable")) g.Selectable = a.Bool("selectable", true);

            var members = new HashSet<ObjectId>(g.GetAllEntityIds());
            int added = 0, removed = 0;
            if (a.Has("handles"))
            {
                foreach (ObjectId id in Acad.Ids(db, a.Strs("handles")))
                {
                    if (members.Contains(id)) continue;
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity)) throw new ToolError($"{id.Handle} is not an entity.");
                    g.Append(id);
                    members.Add(id);
                    added++;
                }
            }
            if (a.Has("remove_handles"))
            {
                foreach (string h in a.Strs("remove_handles"))
                {
                    if (!Acad.TryId(db, h, out ObjectId id) || !members.Contains(id)) continue;
                    g.Remove(id);
                    members.Remove(id);
                    removed++;
                }
            }
            return new Dictionary<string, object>
            {
                ["group"] = name,
                ["created"] = created,
                ["added"] = added,
                ["removed"] = removed,
                ["entities"] = g.NumEntities,
                ["selectable"] = g.Selectable,
            };
        }
    }
}
