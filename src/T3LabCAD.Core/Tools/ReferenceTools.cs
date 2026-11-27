using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>Underlays (image, PDF, DWF, DGN), WBLOCK export, UCS.</summary>
    internal static class ReferenceTools
    {
        static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif", ".jp2", ".ecw" };

        public static void Register()
        {
            Registry.Add("attach_underlay", "Attach image / PDF / DWF / DGN", ToolKind.Write,
                "Attach a raster image (png, jpg, tif, bmp...), a PDF page, a DWF / DWFx sheet or a DGN model as an underlay (IMAGEATTACH / " +
                "PDFATTACH...). Images: width in drawing units (default the pixel width). Underlays: scale (default 1). Not converted to " +
                "geometry (PDFIMPORT is run_command).",
                ObjR("path", Space,
                    P("path", Str("File to attach")),
                    P("position", Pt("Insertion point, lower left (default [0, 0])")),
                    P("width", Num("Image: width in drawing units")),
                    P("scale", Num("PDF / DWF / DGN: scale (default 1)")),
                    P("rotation", Num("Rotation in degrees (default 0)")),
                    P("page", Str("PDF: page number (default 1); DWF: sheet name; DGN: model name")),
                    P("name", Str("Definition name (default the file name)")),
                    P("layer", Str("Layer (created if missing)"))),
                AttachUnderlay);

            Registry.Add("export_entities", "Write entities to a drawing (WBLOCK)", ToolKind.Write,
                "WBLOCK: write entities (handles, all from one space) or a block definition (block) to a new .dwg, with the layers and " +
                "styles they use. The drawing itself does not change. version saves an older format.",
                ObjR("path",
                    P("path", Str("New .dwg path")),
                    Handles,
                    P("block", Str("Or: a block definition to write out")),
                    P("base_point", Pt("handles: base point that becomes 0,0 of the new drawing (default [0, 0])")),
                    P("version", Enum("File format (default current)", "2018", "2013", "2010", "2007", "2004", "2000")),
                    P("overwrite", Bool("Replace an existing file (default false)"))),
                ExportEntities, tx: false, writesFiles: true);

            Registry.Add("set_ucs", "User coordinate system", ToolKind.Write,
                "UCS of the current view. get: origin and axes, the named UCS list and the drawing geolocation (if set); world; " +
                "set {origin, x_axis or rotation (degrees about Z)}; save {name} the current UCS; restore {name}. Tools always take " +
                "and return WCS points; this only changes what the user sees and UCS-based commands.",
                ObjR("action",
                    P("action", Enum("What to do", "get", "world", "set", "save", "restore")),
                    P("origin", Pt("set: new origin (WCS)")),
                    P("x_axis", Pt("set: direction of the new X axis [dx, dy]")),
                    P("rotation", Num("set: rotate the X axis by this many degrees about Z (instead of x_axis)")),
                    P("name", Str("save / restore: named UCS")),
                    P("replace", Bool("save: overwrite a UCS with this name (default false)"))),
                SetUcs, scope: ToolScope.Active);
        }

        // ---------------------------------------------------------------- underlays

        static object AttachUnderlay(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string path = DocumentTools.ResolvePath(a.ReqStr("path"));
            if (!File.Exists(path)) throw new ToolError($"File not found: {path}");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            Point3d position = a.Pt("position", Point3d.Origin);
            double rotation = Geo.Rad(a.Num("rotation", 0));
            ObjectId layerId = a.Has("layer") ? Acad.EnsureLayer(db, tr, a.ReqStr("layer")) : ObjectId.Null;
            var space = (BlockTableRecord)tr.GetObject(Acad.SpaceId(db, tr, a.Str("space")), OpenMode.ForWrite);
            string name = (a.Str("name") ?? Path.GetFileNameWithoutExtension(path)).Trim();

            Entity created;
            string kind;
            if (ImageExtensions.Contains(ext))
            {
                kind = "image";
                created = AttachImage(c, space, path, name, position, rotation, a);
            }
            else if (ext == ".pdf" || ext == ".dwf" || ext == ".dwfx" || ext == ".dgn")
            {
                kind = ext == ".pdf" ? "pdf" : ext == ".dgn" ? "dgn" : "dwf";
                created = AttachFileUnderlay(c, space, path, name, kind, position, rotation, a);
            }
            else throw new ToolError("Attach an image (png, jpg, tif, bmp, gif), a .pdf, a .dwf / .dwfx or a .dgn.");

            if (!layerId.IsNull) created.LayerId = layerId;
            var result = new Dictionary<string, object>
            {
                ["handle"] = created.Handle.ToString(),
                ["kind"] = kind,
                ["file"] = path,
            };
            Extents3d? box = created.Bounds;
            if (box.HasValue) result["bbox"] = new[] { Geo.P(box.Value.MinPoint), Geo.P(box.Value.MaxPoint) };
            return result;
        }

        static Entity AttachImage(ToolContext c, BlockTableRecord space, string path, string name, Point3d position, double rotation, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            ObjectId dictId = RasterImageDef.GetImageDictionary(db);
            if (dictId.IsNull) dictId = RasterImageDef.CreateImageDictionary(db);
            var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);

            ObjectId defId;
            RasterImageDef def;
            if (dict.Contains(name) && tr.GetObject(dict.GetAt(name), OpenMode.ForRead) is RasterImageDef existing &&
                string.Equals(existing.SourceFileName, path, StringComparison.OrdinalIgnoreCase))
            {
                existing.UpgradeOpen(); // AssociateRasterDef adds a reactor to it
                def = existing;
                defId = existing.ObjectId;
            }
            else
            {
                if (dict.Contains(name)) name = RasterImageDef.SuggestName(dict, name);
                def = new RasterImageDef { SourceFileName = path };
                try { def.Load(); }
                catch (Autodesk.AutoCAD.Runtime.Exception ae)
                {
                    def.Dispose();
                    throw new ToolError($"AutoCAD could not load the image ({ae.ErrorStatus}).");
                }
                dict.UpgradeOpen();
                defId = dict.SetAt(name, def);
                tr.AddNewlyCreatedDBObject(def, true);
            }

            Vector2d pixels = def.Size;
            if (pixels.X <= 0 || pixels.Y <= 0) throw new ToolError("The image has no size (not loaded).");
            double width = a.Has("width") ? a.ReqNum("width") : pixels.X;
            if (width <= 0) throw new ToolError("width must be greater than 0.");
            double height = width * pixels.Y / pixels.X;
            var u = new Vector3d(width, 0, 0).RotateBy(rotation, Vector3d.ZAxis);
            var v = new Vector3d(0, height, 0).RotateBy(rotation, Vector3d.ZAxis);

            var image = new RasterImage();
            image.SetDatabaseDefaults(db);
            image.ImageDefId = defId;
            image.Orientation = new CoordinateSystem3d(position, u, v);
            image.ShowImage = true;
            Acad.Append(space, image, tr);
            image.AssociateRasterDef(def);
            return image;
        }

        static Entity AttachFileUnderlay(ToolContext c, BlockTableRecord space, string path, string name, string kind,
            Point3d position, double rotation, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            UnderlayDefinition def;
            UnderlayReference reference;
            string item = a.Str("page");
            switch (kind)
            {
                case "pdf":
                    def = new PdfDefinition();
                    reference = new PdfReference();
                    item = item ?? "1";
                    break;
                case "dgn":
                    def = new DgnDefinition();
                    reference = new DgnReference();
                    break;
                default:
                    def = new DwfDefinition();
                    reference = new DwfReference();
                    break;
            }
            try
            {
                def.SourceFileName = path;
                if (!string.IsNullOrEmpty(item)) def.ItemName = item;

                string key = UnderlayDefinition.GetDictionaryKey(def.GetType());
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                DBDictionary defs;
                if (nod.Contains(key)) defs = (DBDictionary)tr.GetObject(nod.GetAt(key), OpenMode.ForWrite);
                else
                {
                    nod.UpgradeOpen();
                    defs = new DBDictionary();
                    nod.SetAt(key, defs);
                    tr.AddNewlyCreatedDBObject(defs, true);
                }
                string defName = string.IsNullOrEmpty(item) ? name : name + " - " + item;
                for (int i = 2; defs.Contains(defName); i++) defName = (string.IsNullOrEmpty(item) ? name : name + " - " + item) + " (" + i + ")";
                ObjectId defId = defs.SetAt(defName, def);
                tr.AddNewlyCreatedDBObject(def, true);
                try { def.Load(null); }
                catch (Autodesk.AutoCAD.Runtime.Exception ae)
                {
                    throw new ToolError($"AutoCAD could not load {Path.GetFileName(path)}{(item != null ? " (" + item + ")" : "")}: {ae.ErrorStatus}.");
                }

                double scale = a.Num("scale", 1);
                if (scale <= 0) throw new ToolError("scale must be greater than 0.");
                reference.SetDatabaseDefaults(db);
                reference.DefinitionId = defId;
                reference.Position = position;
                reference.Rotation = rotation;
                reference.ScaleFactors = new Scale3d(scale);
                Acad.Append(space, reference, tr);
                return reference;
            }
            catch
            {
                if (def.ObjectId.IsNull) def.Dispose();
                if (reference.ObjectId.IsNull) reference.Dispose();
                throw;
            }
        }

        // ---------------------------------------------------------------- WBLOCK

        static object ExportEntities(ToolContext c, Args a)
        {
            var db = c.Db;
            string path = DocumentTools.ResolvePath(a.ReqStr("path"), ".dwg");
            if (!path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) throw new ToolError("path must be a .dwg.");
            if (c.FilePath != null && string.Equals(path, c.FilePath, StringComparison.OrdinalIgnoreCase))
                throw new ToolError("The target is the drawing itself.");
            if (DocumentTools.Find(path) != null) throw new ToolError($"{path} is open in AutoCAD: close it first.");
            if (File.Exists(path) && !a.Bool("overwrite", false)) throw new ToolError($"{path} already exists. Pass overwrite=true to replace it.");
            if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ToolError($"Folder not found: {Path.GetDirectoryName(path)}");
            DwgVersion version = DocumentTools.DwgVersionOf(a.Str("version"));

            Database output;
            int count;
            if (a.Has("block"))
            {
                string name = a.ReqStr("block");
                ObjectId btrId;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    btrId = Acad.SpaceId(db, tr, "block:" + name); // checks it is a real block definition
                    count = ((BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead)).Cast<ObjectId>().Count();
                    tr.Commit();
                }
                output = db.Wblock(btrId);
            }
            else
            {
                List<ObjectId> ids = Acad.Ids(db, a.Strs("handles"));
                if (ids.Count == 0) throw new ToolError("Pass handles (entities of one space) or block.");
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId owner = tr.GetObject(ids[0], OpenMode.ForRead).OwnerId;
                    if (ids.Any(id => tr.GetObject(id, OpenMode.ForRead).OwnerId != owner))
                        throw new ToolError("All entities must be in the same space.");
                    tr.Commit();
                }
                count = ids.Count;
                output = db.Wblock(new ObjectIdCollection(ids.ToArray()), a.Pt("base_point", Point3d.Origin));
            }
            using (output)
            {
                output.SaveAs(path, version);
            }
            return new Dictionary<string, object>
            {
                ["written"] = path,
                ["entities"] = count,
                ["size_kb"] = (long)Math.Ceiling(new FileInfo(path).Length / 1024.0),
            };
        }

        // ---------------------------------------------------------------- UCS

        static object UcsInfo(Matrix3d ucs)
        {
            CoordinateSystem3d cs = ucs.CoordinateSystem3d;
            return new Dictionary<string, object>
            {
                ["origin"] = Geo.P(cs.Origin),
                ["x_axis"] = Geo.V(cs.Xaxis),
                ["y_axis"] = Geo.V(cs.Yaxis),
                ["is_world"] = ucs.IsEqualTo(Matrix3d.Identity),
            };
        }

        static object SetUcs(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            var ed = c.Ed;
            string action = a.ReqStr("action").Trim().ToLowerInvariant();
            var table = (UcsTable)tr.GetObject(db.UcsTableId, OpenMode.ForRead);
            switch (action)
            {
                case "get":
                {
                    var result = new Dictionary<string, object> { ["current"] = UcsInfo(ed.CurrentUserCoordinateSystem) };
                    var names = new List<object>();
                    foreach (ObjectId id in table)
                        if (tr.GetObject(id, OpenMode.ForRead) is UcsTableRecord u && !u.IsErased) names.Add(u.Name);
                    result["named"] = names;
                    try
                    {
                        ObjectId geoId = db.GeoDataObject;
                        if (!geoId.IsNull && tr.GetObject(geoId, OpenMode.ForRead) is GeoLocationData geo)
                        {
                            result["geolocation"] = new Dictionary<string, object>
                            {
                                ["coordinate_system"] = geo.CoordinateSystem,
                                ["design_point"] = Geo.P(geo.DesignPoint),
                                ["reference_point"] = Geo.P(geo.ReferencePoint),
                                ["north_direction_deg"] = Geo.Deg(geo.NorthDirection),
                            };
                        }
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // no geolocation in this drawing
                    }
                    result["insbase"] = Geo.P(db.Insbase);
                    return result;
                }
                case "world":
                    ed.CurrentUserCoordinateSystem = Matrix3d.Identity;
                    return new Dictionary<string, object> { ["current"] = UcsInfo(ed.CurrentUserCoordinateSystem) };
                case "set":
                {
                    Point3d origin = a.Pt("origin", ed.CurrentUserCoordinateSystem.CoordinateSystem3d.Origin);
                    Vector3d x;
                    if (a.Has("x_axis"))
                    {
                        Point3d d = a.ReqPt("x_axis");
                        x = new Vector3d(d.X, d.Y, 0);
                        if (x.Length < 1e-12) throw new ToolError("x_axis must not be zero.");
                    }
                    else x = Vector3d.XAxis.RotateBy(Geo.Rad(a.Num("rotation", 0)), Vector3d.ZAxis);
                    x = x.GetNormal();
                    Vector3d y = Vector3d.ZAxis.CrossProduct(x).GetNormal();
                    ed.CurrentUserCoordinateSystem = Matrix3d.AlignCoordinateSystem(
                        Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis, origin, x, y, Vector3d.ZAxis);
                    return new Dictionary<string, object> { ["current"] = UcsInfo(ed.CurrentUserCoordinateSystem) };
                }
                case "save":
                {
                    string name = a.ReqStr("name").Trim();
                    Acad.ValidateName(name, "UCS");
                    CoordinateSystem3d cs = ed.CurrentUserCoordinateSystem.CoordinateSystem3d;
                    UcsTableRecord rec;
                    if (table.Has(name))
                    {
                        if (!a.Bool("replace", false)) throw new ToolError($"A UCS named '{name}' exists: pass replace=true to overwrite it.");
                        rec = (UcsTableRecord)tr.GetObject(table[name], OpenMode.ForWrite);
                    }
                    else
                    {
                        rec = new UcsTableRecord { Name = name };
                        table.UpgradeOpen();
                        table.Add(rec);
                        tr.AddNewlyCreatedDBObject(rec, true);
                    }
                    rec.Origin = cs.Origin;
                    rec.XAxis = cs.Xaxis;
                    rec.YAxis = cs.Yaxis;
                    return new Dictionary<string, object> { ["saved"] = name, ["ucs"] = UcsInfo(ed.CurrentUserCoordinateSystem) };
                }
                case "restore":
                {
                    string name = a.ReqStr("name").Trim();
                    if (!table.Has(name)) throw new ToolError($"No UCS named '{name}' (action=get lists them).");
                    var rec = (UcsTableRecord)tr.GetObject(table[name], OpenMode.ForRead);
                    Vector3d z = rec.XAxis.CrossProduct(rec.YAxis).GetNormal();
                    ed.CurrentUserCoordinateSystem = Matrix3d.AlignCoordinateSystem(
                        Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis, rec.Origin, rec.XAxis, rec.YAxis, z);
                    return new Dictionary<string, object> { ["restored"] = name, ["current"] = UcsInfo(ed.CurrentUserCoordinateSystem) };
                }
                default:
                    throw new ToolError("action must be get, world, set, save or restore.");
            }
        }
    }
}
