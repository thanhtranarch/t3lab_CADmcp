using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcRx = Autodesk.AutoCAD.Runtime;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    internal static class DocumentTools
    {
        public static void Register()
        {
            Registry.Add("activate_document", "Switch drawing", ToolKind.App,
                "Make another open drawing the active one (all other tools act on the active drawing). " +
                "Match by full path, file name, or file name without extension.",
                ObjR("document", P("document", Str("Drawing name or path, see get_status"))), Activate, needsDocument: false);

            Registry.Add("open_document", "Open drawing", ToolKind.App,
                "Open a .dwg (or .dxf) file in AutoCAD and make it active. If it is already open it is just activated. " +
                "background=true reads a .dwg into memory without a window and without switching: work in it with autocad_document=<name>, " +
                "then save_document / close_document with document=<name>. " +
                "A name without folder is looked up next to the active drawing (list_drawing_files finds drawings).",
                ObjR("path", P("path", Str("Path of the file (.dwg added when there is no extension)")), P("read_only", Bool("Open read-only (default false)")),
                    P("background", Bool("Open out of sight; the active drawing stays active (default false)"))),
                Open, needsDocument: false);

            Registry.Add("new_document", "New drawing", ToolKind.App,
                "Create a new drawing from a template (default acadiso.dwt, metric) and make it active. save_as also saves it to a .dwg right away.",
                Obj(P("template", Str("Template file name or path, e.g. acadiso.dwt or acad.dwt")),
                    P("save_as", Str("Optional .dwg path to save the new drawing to (must not exist)"))),
                New, needsDocument: false);

            Registry.Add("save_document", "Save drawing", ToolKind.App,
                "Save the active drawing. No path: save in place. A .dwg path: Save As (the drawing is renamed). " +
                "A .dxf path: export a DXF copy (the open drawing keeps its name). copy=true with a .dwg path: write a copy and keep " +
                "working on the original (needs no unsaved changes). version saves in an older format: 2018 (2018-2027), 2013, 2010, 2007, 2004, 2000.",
                Obj(P("path", Str("Optional target path (.dwg or .dxf)")),
                    P("copy", Bool("Save a copy to path; the open drawing keeps its name (default false)")),
                    P("version", Enum("File format (default: the current format)", "2018", "2013", "2010", "2007", "2004", "2000")),
                    P("overwrite", Bool("copy: replace an existing file at path (default false)")),
                    P("document", Str("A drawing opened in the background (name or path): saved to its file, or to path (default: the active drawing)"))),
                Save, needsDocument: false);

            Registry.Add("close_document", "Close drawing", ToolKind.App,
                "Close a drawing (default: the active one). Refuses when there are unsaved changes unless save=true or discard_changes=true.",
                Obj(P("document", Str("Drawing name or path, also one opened in the background (default active)")),
                    P("save", Bool("Save before closing")),
                    P("discard_changes", Bool("Close without saving: unsaved changes are lost"))),
                Close, needsDocument: false, destructive: true);

            Registry.Add("list_layouts", "List layouts", ToolKind.Read,
                "Model and paper space layouts (sheets) of the drawing with tab order, viewport count, paper size and plotter.",
                Obj(), ListLayouts);

            Registry.Add("activate_layout", "Switch layout", ToolKind.Write,
                "Make a layout current (Model or a paper space layout name).",
                ObjR("name", P("name", Str("Layout name, or Model"))), ActivateLayout, tx: false, scope: ToolScope.Active);
        }

        internal static Document Find(string name)
        {
            foreach (Document d in AcApp.DocumentManager)
            {
                if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(d.Name), name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(d.Name), name, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        static object DocInfo(Document d) => new Dictionary<string, object>
        {
            ["name"] = Path.GetFileName(d.Name),
            ["path"] = d.Name,
            ["read_only"] = d.IsReadOnly,
        };

        static object Activate(ToolContext c, Args a)
        {
            string name = a.ReqStr("document");
            Document d = Find(name) ?? throw new ToolError($"No open drawing matches '{name}'. See get_status for the open drawings.");
            if (AcApp.DocumentManager.MdiActiveDocument != d) AcApp.DocumentManager.MdiActiveDocument = d;
            return new Dictionary<string, object> { ["active"] = DocInfo(d) };
        }

        /// <summary>
        /// Full path for a file argument: quotes trimmed, a bare or relative name is taken next to the
        /// active drawing, defaultExt added when there is no extension.
        /// </summary>
        internal static string ResolvePath(string path, string defaultExt = null)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) throw new ToolError("The path is empty.");
            if (!Path.IsPathRooted(path))
            {
                Document active = AcApp.DocumentManager.MdiActiveDocument;
                if (active == null || !active.IsNamedDrawing)
                    throw new ToolError($"'{path}' is not a full path, and there is no saved active drawing to take its folder from.");
                path = Path.Combine(Path.GetDirectoryName(active.Name), path);
            }
            if (defaultExt != null && Path.GetExtension(path).Length == 0) path += defaultExt;
            try { return Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new ToolError($"Invalid path '{path}': {ex.Message}");
            }
        }

        /// <summary>"2018" (also 2019-2027: same format), "2013", ... -> the file format year, or null for the current format.</summary>
        static int? FormatYear(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            string s = version.Trim().ToLowerInvariant();
            if (s == "current" || s == "native" || s == "latest") return null;
            string digits = new string(s.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out int year) || year < 2000 || year > 2100)
                throw new ToolError($"Unknown version '{version}'. Use 2018, 2013, 2010, 2007, 2004 or 2000.");
            foreach (int f in new[] { 2018, 2013, 2010, 2007, 2004, 2000 })
                if (year >= f) return f;
            return 2000;
        }

        internal static DwgVersion DwgVersionOf(string version)
        {
            switch (FormatYear(version))
            {
                case null: return DwgVersion.Current;
                case 2018: return DwgVersion.AC1032;
                case 2013: return DwgVersion.AC1027;
                case 2010: return DwgVersion.AC1024;
                case 2007: return DwgVersion.AC1021;
                case 2004: return DwgVersion.AC1800;
                default: return DwgVersion.AC1015;
            }
        }

        /// <summary>ActiveX AcSaveAsType for a .dwg of that format year (ac2018_dwg = 64 ... ac2000_dwg = 12).</summary>
        static int SaveAsType(int year)
        {
            switch (year)
            {
                case 2018: return 64;
                case 2013: return 60;
                case 2010: return 48;
                case 2007: return 36;
                case 2004: return 24;
                default: return 12;
            }
        }

        static object Open(ToolContext c, Args a)
        {
            string path = ResolvePath(a.ReqStr("path"), ".dwg");
            if (!File.Exists(path)) throw new ToolError($"File not found: {path}. list_drawing_files shows the drawings in a folder.");
            BackgroundDrawings.Drawing hidden = BackgroundDrawings.Find(path);
            if (a.Bool("background", false))
            {
                if (Find(path) != null) throw new ToolError($"{Path.GetFileName(path)} is open in AutoCAD already: work in it with autocad_document='{Path.GetFileName(path)}'.");
                BackgroundDrawings.Drawing bg = BackgroundDrawings.Load(path, a.Bool("read_only", false));
                var r = BackgroundDrawings.Info(bg);
                r["note"] = $"Work in it with autocad_document='{Path.GetFileName(path)}'; save_document / close_document with document='{Path.GetFileName(path)}'.";
                return new Dictionary<string, object> { ["opened"] = r };
            }
            if (hidden != null)
            {
                if (hidden.Modified) throw new ToolError($"{Path.GetFileName(path)} is open in the background with unsaved changes: save_document or close_document it (document=...) first.");
                BackgroundDrawings.Close(hidden);   // the editor takes over the file
            }
            Document existing = Find(path);
            if (existing != null)
            {
                AcApp.DocumentManager.MdiActiveDocument = existing;
                return new Dictionary<string, object> { ["already_open"] = true, ["active"] = DocInfo(existing) };
            }
            Document d = AcApp.DocumentManager.Open(path, a.Bool("read_only", false));
            return new Dictionary<string, object> { ["opened"] = DocInfo(d) };
        }

        static object New(ToolContext c, Args a)
        {
            string template = a.Str("template");
            string saveAs = a.Has("save_as") ? ResolvePath(a.ReqStr("save_as"), ".dwg") : null;
            if (saveAs != null)
            {
                if (!saveAs.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) throw new ToolError("save_as must be a .dwg path.");
                if (File.Exists(saveAs)) throw new ToolError($"{saveAs} already exists. Use open_document to open it, or pick another name.");
                if (!Directory.Exists(Path.GetDirectoryName(saveAs))) throw new ToolError($"Folder not found: {Path.GetDirectoryName(saveAs)}");
            }
            Document d;
            if (!string.IsNullOrEmpty(template)) d = AcApp.DocumentManager.Add(template);
            else
            {
                try { d = AcApp.DocumentManager.Add("acadiso.dwt"); }
                catch (AcRx.Exception) { d = AcApp.DocumentManager.Add("acad.dwt"); }
            }
            if (saveAs != null)
            {
                dynamic acadDoc = d.GetAcadDocument();
                acadDoc.SaveAs(saveAs);
            }
            return new Dictionary<string, object> { ["created"] = DocInfo(d) };
        }

        static object Save(ToolContext c, Args a)
        {
            string path = a.Has("path") ? ResolvePath(a.ReqStr("path"), ".dwg") : null;
            int? year = FormatYear(a.Str("version"));
            string format = year?.ToString() ?? "current";
            if (a.Has("document"))
            {
                string name = a.ReqStr("document");
                BackgroundDrawings.Drawing hidden = BackgroundDrawings.Find(name);
                if (hidden == null)
                {
                    Document open = Find(name);
                    if (open == null) throw new ToolError($"No drawing '{name}' is open (get_status lists them).");
                    if (open != AcApp.DocumentManager.MdiActiveDocument)
                        throw new ToolError($"{Path.GetFileName(open.Name)} is open in AutoCAD but not active: activate_document it first, or close_document it with save=true.");
                }
                else
                {
                    if (path != null && !path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) throw new ToolError("A background drawing saves to a .dwg.");
                    if (path != null && File.Exists(path) && !string.Equals(path, hidden.Path, StringComparison.OrdinalIgnoreCase) && !a.Bool("overwrite", false))
                        throw new ToolError($"{path} already exists. Pass overwrite=true to replace it.");
                    BackgroundDrawings.Save(hidden, path, year == null ? (DwgVersion?)null : DwgVersionOf(a.Str("version")));
                    return new Dictionary<string, object> { ["saved"] = hidden.Path, ["background"] = true, ["version"] = format };
                }
            }
            Document doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new ToolError("No drawing is open in AutoCAD.");
            if (path != null && path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
            {
                using (doc.LockDocument()) doc.Database.DxfOut(path, 16, DwgVersionOf(a.Str("version")));
                return new Dictionary<string, object> { ["exported"] = path, ["format"] = "dxf", ["version"] = format };
            }
            if (path != null && !path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                throw new ToolError("path must end with .dwg or .dxf.");
            if (a.Bool("copy", false)) return SaveCopy(doc, path, a.Str("version"), format, a.Bool("overwrite", false));

            dynamic acadDoc = doc.GetAcadDocument();
            if (string.IsNullOrEmpty(path))
            {
                if (!doc.IsNamedDrawing) throw new ToolError("This drawing has never been saved: pass a .dwg path.");
                if (doc.IsReadOnly) throw new ToolError("The drawing is open read-only: pass a new .dwg path, or copy=true with a path.");
                if (year == null) acadDoc.Save();
                else acadDoc.SaveAs(doc.Name, SaveAsType(year.Value));
            }
            else
            {
                if (year == null) acadDoc.SaveAs(path);
                else acadDoc.SaveAs(path, SaveAsType(year.Value));
            }
            return new Dictionary<string, object> { ["saved"] = doc.Name, ["version"] = format };
        }

        /// <summary>A copy of the saved file (converted through a side database when another format is asked).</summary>
        static object SaveCopy(Document doc, string path, string version, string format, bool overwrite)
        {
            if (string.IsNullOrEmpty(path)) throw new ToolError("copy=true needs a target .dwg path.");
            if (!doc.IsNamedDrawing) throw new ToolError("This drawing has never been saved: save it with a path first (or export a .dxf copy).");
            dynamic acadDoc = doc.GetAcadDocument();
            if (!(bool)acadDoc.Saved)
                throw new ToolError("The drawing has unsaved changes and a copy is made from the file on disk. Save first (save_document), then copy.");
            if (string.Equals(path, doc.Name, StringComparison.OrdinalIgnoreCase)) throw new ToolError("The copy must have another path than the drawing.");
            if (Find(path) != null) throw new ToolError($"{path} is open in AutoCAD: close it first.");
            if (File.Exists(path) && !overwrite) throw new ToolError($"{path} already exists. Pass overwrite=true to replace it.");
            if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ToolError($"Folder not found: {Path.GetDirectoryName(path)}");

            if (FormatYear(version) == null) File.Copy(doc.Name, path, true);
            else
            {
                using (var side = new Database(false, true))
                {
                    side.ReadDwgFile(doc.Name, FileOpenMode.OpenForReadAndAllShare, true, "");
                    side.CloseInput(true);
                    side.SaveAs(path, DwgVersionOf(version));
                }
            }
            return new Dictionary<string, object> { ["copied_to"] = path, ["from"] = doc.Name, ["version"] = format };
        }

        static object Close(ToolContext c, Args a)
        {
            string name = a.Str("document");
            BackgroundDrawings.Drawing hidden = name == null ? null : BackgroundDrawings.Find(name);
            if (hidden != null)
            {
                bool keep = a.Bool("save", false);
                if (hidden.Modified && !keep && !a.Bool("discard_changes", false))
                    throw new ToolError("The background drawing has unsaved changes. Pass save=true to save them, or discard_changes=true to close and lose them.");
                if (keep) BackgroundDrawings.Save(hidden, null, null);
                bool lost = hidden.Modified;
                BackgroundDrawings.Close(hidden);
                return new Dictionary<string, object> { ["closed"] = hidden.Path, ["background"] = true, ["saved"] = keep, ["discarded_changes"] = lost };
            }
            Document d = name == null ? AcApp.DocumentManager.MdiActiveDocument ?? throw new ToolError("No drawing is open in AutoCAD.")
                : Find(name) ?? throw new ToolError($"No open drawing matches '{name}'.");
            string closed = d.Name;
            bool save = a.Bool("save", false), discard = a.Bool("discard_changes", false);
            dynamic acadDoc = d.GetAcadDocument();
            bool unsaved = !(bool)acadDoc.Saved;

            if (save)
            {
                if (!d.IsNamedDrawing) throw new ToolError("This drawing has never been saved: use save_document with a path first.");
                d.CloseAndSave(d.Name);
            }
            else if (unsaved && !discard)
            {
                throw new ToolError("The drawing has unsaved changes. Pass save=true to save them, or discard_changes=true to close and lose them.");
            }
            else
            {
                d.CloseAndDiscard();
            }
            return new Dictionary<string, object> { ["closed"] = closed, ["saved"] = save, ["discarded_changes"] = unsaved && !save };
        }

        /// <summary>The current layout read from the database itself (any drawing, also a background one).</summary>
        internal static string CurrentLayout(Database db, Transaction tr)
        {
            if (db.TileMode) return "Model";
            var paper = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockPaperSpaceId(db), OpenMode.ForRead);
            return ((Layout)tr.GetObject(paper.LayoutId, OpenMode.ForRead)).LayoutName;
        }

        internal static object ListLayouts(ToolContext c, Args a)
        {
            var layouts = (DBDictionary)c.Tr.GetObject(c.Db.LayoutDictionaryId, OpenMode.ForRead);
            string current = CurrentLayout(c.Db, c.Tr);
            var items = new List<Dictionary<string, object>>();
            foreach (DBDictionaryEntry entry in layouts)
            {
                var lay = (Layout)c.Tr.GetObject(entry.Value, OpenMode.ForRead);
                int viewports = lay.ModelType ? 0 : Math.Max(0, lay.GetViewports().Count - 1);
                items.Add(new Dictionary<string, object>
                {
                    ["name"] = lay.LayoutName,
                    ["tab_order"] = lay.TabOrder,
                    ["current"] = string.Equals(lay.LayoutName, current, StringComparison.OrdinalIgnoreCase),
                    ["model"] = lay.ModelType,
                    ["viewports"] = viewports,
                    ["paper"] = lay.CanonicalMediaName,
                    ["plotter"] = lay.PlotConfigurationName,
                    ["plot_style"] = lay.CurrentStyleSheet,
                });
            }
            return new Dictionary<string, object>
            {
                ["current"] = current,
                ["layouts"] = items.OrderBy(i => (int)i["tab_order"]).Cast<object>().ToList(),
            };
        }

        static object ActivateLayout(ToolContext c, Args a)
        {
            string name = a.ReqStr("name");
            LayoutManager lm = LayoutManager.Current;
            if (!lm.LayoutExists(name)) throw new ToolError($"Layout '{name}' does not exist (see list_layouts).");
            lm.CurrentLayout = name;
            return new Dictionary<string, object> { ["current"] = lm.CurrentLayout };
        }
    }
}
