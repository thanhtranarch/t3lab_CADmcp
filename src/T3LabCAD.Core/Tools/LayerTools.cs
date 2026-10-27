using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    internal static class LayerTools
    {
        public static void Register()
        {
            Registry.Add("list_layers", "List layers", ToolKind.Read,
                "Layers of the active drawing: color, linetype, lineweight, on/off, frozen, locked, plot, current. " +
                "Xref layers (NAME|LAYER) are skipped unless include_xref=true.",
                Obj(P("name", Str("Wildcard filter, e.g. A-WALL* (default all)")),
                    P("include_xref", Bool("Include xref-dependent layers (default false)")),
                    P("with_counts", Bool("Also count entities per layer in model and paper space (slower)"))),
                ListLayers);

            Registry.Add("set_layer", "Create or edit layer", ToolKind.Write,
                "Create a layer (if missing) and/or change it: rename, color, linetype, lineweight, on, frozen, locked, plot, description, make current.",
                ObjR("name",
                    P("name", Str("Layer name")),
                    P("create", Bool("Create the layer when it does not exist (default true)")),
                    P("new_name", Str("Rename the layer")),
                    P("color", Any("ACI 1-255, color name, #RRGGBB or [r, g, b]")),
                    P("linetype", Str("Linetype name (loaded from acadiso.lin if needed)")),
                    P("lineweight", Any("Hundredths of a mm, e.g. 25 = 0.25 mm, or Default")),
                    P("on", Bool("Layer on")),
                    P("frozen", Bool("Layer frozen")),
                    P("locked", Bool("Layer locked")),
                    P("plot", Bool("Layer plots")),
                    P("description", Str("Layer description")),
                    P("make_current", Bool("Make it the current layer"))),
                SetLayer);

            Registry.Add("delete_layer", "Delete layer", ToolKind.Write,
                "Delete an unused layer. With merge_into, first moves every entity on it (including inside block definitions) to that layer.",
                ObjR("name", P("name", Str("Layer to delete")), P("merge_into", Str("Layer that receives its entities"))),
                DeleteLayer, destructive: true);
        }

        static object ListLayers(ToolContext c, Args a)
        {
            var tr = c.Tr;
            string filter = a.Str("name");
            bool includeXref = a.Bool("include_xref", false);
            Dictionary<ObjectId, int> counts = a.Bool("with_counts", false) ? CountByLayer(c) : null;

            var table = (LayerTable)tr.GetObject(c.Db.LayerTableId, OpenMode.ForRead);
            var layers = new List<object>();
            int skippedXref = 0;
            foreach (ObjectId id in table)
            {
                var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (l.IsErased) continue;
                if (l.IsDependent && !includeXref) { skippedXref++; continue; }
                if (filter != null && !Wild.Match(l.Name, filter)) continue;
                var ltr = tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead) as LinetypeTableRecord;
                var item = new Dictionary<string, object>
                {
                    ["name"] = l.Name,
                    ["color"] = Acad.ColorOf(l.Color),
                    ["linetype"] = ltr?.Name ?? "",
                    ["lineweight"] = Acad.LineWeightOf(l.LineWeight),
                    ["on"] = !l.IsOff,
                    ["frozen"] = l.IsFrozen,
                    ["locked"] = l.IsLocked,
                    ["plot"] = l.IsPlottable,
                };
                if (id == c.Db.Clayer) item["current"] = true;
                if (!string.IsNullOrEmpty(l.Description)) item["description"] = l.Description;
                if (counts != null) item["entities"] = counts.TryGetValue(id, out int n) ? n : 0;
                layers.Add(item);
            }
            var result = new Dictionary<string, object> { ["count"] = layers.Count, ["layers"] = layers };
            if (skippedXref > 0) result["xref_layers_skipped"] = skippedXref;
            return result;
        }

        static Dictionary<ObjectId, int> CountByLayer(ToolContext c)
        {
            var counts = new Dictionary<ObjectId, int>();
            var layouts = (DBDictionary)c.Tr.GetObject(c.Db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                var lay = (Layout)c.Tr.GetObject(entry.Value, OpenMode.ForRead);
                foreach (ObjectId id in (BlockTableRecord)c.Tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead))
                {
                    if (c.Tr.GetObject(id, OpenMode.ForRead) is Entity e)
                        counts[e.LayerId] = counts.TryGetValue(e.LayerId, out int n) ? n + 1 : 1;
                }
            }
            return counts;
        }

        static object SetLayer(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string name = a.ReqStr("name");
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            bool created = false;
            ObjectId id;
            if (table.Has(name)) id = table[name];
            else
            {
                if (!a.Bool("create", true)) throw new ToolError($"Layer '{name}' does not exist.");
                id = Acad.EnsureLayer(db, tr, name);
                created = true;
            }

            var layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            var changed = new List<string>();
            if (a.Has("new_name"))
            {
                string newName = a.ReqStr("new_name");
                if (layer.Name == "0" || layer.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase))
                    throw new ToolError($"Layer '{layer.Name}' cannot be renamed.");
                Acad.ValidateName(newName, "layer");
                if (table.Has(newName)) throw new ToolError($"A layer named '{newName}' already exists.");
                layer.Name = newName;
                changed.Add("name");
            }
            if (a.Has("color"))
            {
                var color = Acad.ParseColor(a.Raw("color"));
                if (color.IsByLayer || color.IsByBlock) throw new ToolError("A layer color must be a real color, not ByLayer/ByBlock.");
                layer.Color = color;
                changed.Add("color");
            }
            if (a.Has("linetype")) { layer.LinetypeObjectId = Acad.LinetypeId(db, tr, a.ReqStr("linetype")); changed.Add("linetype"); }
            if (a.Has("lineweight")) { layer.LineWeight = Acad.ParseLineWeight(a.Raw("lineweight")); changed.Add("lineweight"); }
            if (a.Has("description")) { layer.Description = a.Str("description"); changed.Add("description"); }
            if (a.Has("plot")) { layer.IsPlottable = a.Bool("plot", true); changed.Add("plot"); }
            if (a.Has("locked")) { layer.IsLocked = a.Bool("locked", false); changed.Add("locked"); }
            if (a.Has("on")) { layer.IsOff = !a.Bool("on", true); changed.Add("on"); c.NeedsRegen = true; }
            if (a.Has("frozen"))
            {
                bool frozen = a.Bool("frozen", false);
                if (frozen && db.Clayer == id) throw new ToolError("The current layer cannot be frozen. Make another layer current first.");
                layer.IsFrozen = frozen;
                changed.Add("frozen");
                c.NeedsRegen = true;
            }
            if (a.Bool("make_current", false))
            {
                if (layer.IsFrozen) throw new ToolError("A frozen layer cannot be made current.");
                db.Clayer = id;
                changed.Add("current");
            }
            return new Dictionary<string, object> { ["layer"] = layer.Name, ["created"] = created, ["changed"] = changed };
        }

        static object DeleteLayer(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string name = a.ReqStr("name");
            if (name == "0" || name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase))
                throw new ToolError($"Layer '{name}' cannot be deleted.");
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!table.Has(name)) throw new ToolError($"Layer '{name}' does not exist.");
            ObjectId id = table[name];
            if (db.Clayer == id) throw new ToolError("The current layer cannot be deleted. Make another layer current first.");

            int moved = 0;
            string target = a.Str("merge_into");
            if (target != null)
            {
                if (target.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new ToolError("merge_into must be a different layer.");
                ObjectId targetId = Acad.EnsureLayer(db, tr, target);
                ((LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite)).IsLocked = false;
                var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in blocks)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (btr.IsFromExternalReference || btr.IsDependent) continue;
                    foreach (ObjectId eid in btr)
                    {
                        if (tr.GetObject(eid, OpenMode.ForRead) is Entity e && e.LayerId == id)
                        {
                            e.UpgradeOpen();
                            e.LayerId = targetId;
                            moved++;
                        }
                    }
                }
            }

            var purgeable = new ObjectIdCollection { id };
            db.Purge(purgeable);
            if (purgeable.Count == 0)
                throw new ToolError($"Layer '{name}' is still in use (entities, block definitions or xrefs). Pass merge_into=<layer> to move its entities first.");
            tr.GetObject(id, OpenMode.ForWrite).Erase();
            return new Dictionary<string, object> { ["deleted"] = name, ["entities_moved"] = moved, ["merged_into"] = target };
        }
    }
}
