using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>External references: attach, detach, reload, unload, bind, repath. List: list_named_objects kind=xref.</summary>
    internal static class XrefTools
    {
        public static void Register()
        {
            Registry.Add("attach_xref", "Attach xref", ToolKind.Write,
                "Attach (or overlay) a .dwg as an external reference and insert it. The path is stored relative to this drawing " +
                "when relative_path=true and both are on the same drive.",
                ObjR("path", Space,
                    P("path", Str("Drawing to reference (.dwg)")),
                    P("name", Str("Xref name (default: file name)")),
                    P("position", Pt("Insertion point (default [0, 0])")),
                    P("scale", Num("Uniform scale (default 1)")),
                    P("rotation", Num("Rotation in degrees (default 0)")),
                    P("overlay", Bool("Overlay instead of attach: not seen when this drawing is itself referenced (default false)")),
                    P("relative_path", Bool("Store a relative path (default false)")),
                    P("layer", Str("Layer for the xref insert (created if missing)"))),
                Attach);

            Registry.Add("manage_xref", "Manage xrefs", ToolKind.Write,
                "Act on attached xrefs by name (see list_named_objects kind=xref): detach (remove it and all its inserts), reload, unload, " +
                "bind (make it a normal block, names become XREF$0$NAME), insert (bind with plain names), repath (new_path, then reload).",
                ObjR("names,action",
                    P("names", Strs("Xref names")),
                    P("action", Enum("What to do", "detach", "reload", "unload", "bind", "insert", "repath")),
                    P("new_path", Str("repath: the new file path"))),
                Manage, tx: false, destructive: true);
        }

        internal static Dictionary<string, object> Info(BlockTableRecord btr)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = btr.Name,
                ["path"] = btr.PathName,
                ["status"] = Status(btr.XrefStatus),
                ["inserts"] = btr.GetBlockReferenceIds(true, false).Count,
            };
            if (btr.IsFromOverlayReference) d["overlay"] = true;
            if (btr.IsDependent) d["nested"] = true;
            return d;
        }

        static string Status(XrefStatus s)
        {
            switch (s)
            {
                case XrefStatus.Resolved: return "loaded";
                case XrefStatus.Unloaded: return "unloaded";
                case XrefStatus.FileNotFound: return "not_found";
                case XrefStatus.Unresolved: return "unresolved";
                case XrefStatus.Unreferenced: return "unreferenced";
                default: return s.ToString();
            }
        }

        static object Attach(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string path = DocumentTools.ResolvePath(a.ReqStr("path"), ".dwg");
            if (!File.Exists(path)) throw new ToolError($"File not found: {path}");
            if (c.FilePath != null && string.Equals(path, c.FilePath, StringComparison.OrdinalIgnoreCase))
                throw new ToolError("A drawing cannot reference itself.");
            string name = (a.Str("name") ?? Path.GetFileNameWithoutExtension(path)).Trim();
            Acad.ValidateName(name, "xref");

            // every check before AttachXref: rolling back a loaded xref through an aborted transaction is best avoided
            double scale = a.Num("scale", 1);
            if (Math.Abs(scale) < 1e-12) throw new ToolError("scale cannot be 0.");
            ObjectId spaceId = Acad.SpaceId(db, tr, a.Str("space"));
            ObjectId layerId = a.Has("layer") ? Acad.EnsureLayer(db, tr, a.ReqStr("layer")) : ObjectId.Null;
            string relative = null;
            if (a.Bool("relative_path", false))
            {
                relative = c.FilePath != null ? Relative(Path.GetDirectoryName(c.FilePath), path) : null;
                if (relative == null) throw new ToolError("relative_path needs a saved host drawing on the same drive as the xref.");
            }

            var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            ObjectId xrefId;
            if (blocks.Has(name))
            {
                var existing = (BlockTableRecord)tr.GetObject(blocks[name], OpenMode.ForRead);
                if (!existing.IsFromExternalReference) throw new ToolError($"A block named '{name}' already exists: pass another name.");
                xrefId = existing.ObjectId; // already attached: just insert it again
            }
            else
            {
                xrefId = a.Bool("overlay", false) ? db.OverlayXref(path, name) : db.AttachXref(path, name);
                if (xrefId.IsNull) throw new ToolError($"AutoCAD could not attach {path}.");
                if (relative != null) ((BlockTableRecord)tr.GetObject(xrefId, OpenMode.ForWrite)).PathName = relative;
            }

            var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForWrite);
            var br = new BlockReference(a.Pt("position", Point3d.Origin), xrefId)
            {
                Rotation = Geo.Rad(a.Num("rotation", 0)),
                ScaleFactors = new Scale3d(scale),
            };
            br.SetDatabaseDefaults(db);
            if (!layerId.IsNull) br.LayerId = layerId;
            Acad.Append(space, br, tr);

            var info = Info((BlockTableRecord)tr.GetObject(xrefId, OpenMode.ForRead));
            info["insert_handle"] = br.Handle.ToString();
            info["space"] = Acad.SpaceName(tr, spaceId);
            return info;
        }

        /// <summary>..\Folder\file.dwg style path from the host folder, or null on another drive.</summary>
        static string Relative(string fromFolder, string path)
        {
            if (!string.Equals(Path.GetPathRoot(fromFolder), Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) return null;
            var from = new Uri(fromFolder.TrimEnd('\\') + "\\");
            string rel = Uri.UnescapeDataString(from.MakeRelativeUri(new Uri(path)).ToString()).Replace('/', '\\');
            return rel.StartsWith("..", StringComparison.Ordinal) ? rel : ".\\" + rel;
        }

        static object Manage(ToolContext c, Args a)
        {
            var db = c.Db;
            string action = (a.ReqStr("action")).Trim().ToLowerInvariant();
            List<string> names = a.Strs("names");
            if (names.Count == 0) throw new ToolError("'names' is empty.");

            var ids = new List<ObjectId>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (string name in names)
                {
                    if (!blocks.Has(name)) throw new ToolError($"No xref named '{name}' (see list_named_objects kind=xref).");
                    var btr = (BlockTableRecord)tr.GetObject(blocks[name], OpenMode.ForRead);
                    if (!btr.IsFromExternalReference) throw new ToolError($"'{name}' is a block, not an xref.");
                    if (btr.IsDependent) throw new ToolError($"'{name}' is nested in another xref: act on its parent.");
                    ids.Add(btr.ObjectId);
                }
                tr.Commit();
            }

            switch (action)
            {
                case "detach":
                    foreach (ObjectId id in ids) db.DetachXref(id);
                    c.NeedsRegen = true;
                    return new Dictionary<string, object> { ["action"] = action, ["detached"] = names };
                case "reload":
                    db.ReloadXrefs(new ObjectIdCollection(ids.ToArray()));
                    break;
                case "unload":
                    db.UnloadXrefs(new ObjectIdCollection(ids.ToArray()));
                    break;
                case "bind":
                case "insert":
                    db.BindXrefs(new ObjectIdCollection(ids.ToArray()), action == "insert");
                    c.NeedsRegen = true;
                    return new Dictionary<string, object> { ["action"] = action, ["bound"] = names };
                case "repath":
                    if (ids.Count != 1) throw new ToolError("repath takes one xref name.");
                    string newPath = DocumentTools.ResolvePath(a.ReqStr("new_path"), ".dwg");
                    if (!File.Exists(newPath)) throw new ToolError($"File not found: {newPath}");
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        ((BlockTableRecord)tr.GetObject(ids[0], OpenMode.ForWrite)).PathName = newPath;
                        tr.Commit();
                    }
                    db.ReloadXrefs(new ObjectIdCollection(ids.ToArray()));
                    break;
                default:
                    throw new ToolError("action must be detach, reload, unload, bind, insert or repath.");
            }

            c.NeedsRegen = true;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var xrefs = ids.Select(id => (object)Info((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead))).ToList();
                tr.Commit();
                return new Dictionary<string, object> { ["action"] = action, ["xrefs"] = xrefs };
            }
        }
    }
}
