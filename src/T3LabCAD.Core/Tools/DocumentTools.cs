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
                "Open a .dwg (or .dxf) file in AutoCAD and make it active. If it is already open it is just activated.",
                ObjR("path", P("path", Str("Full path of the file")), P("read_only", Bool("Open read-only (default false)"))),
                Open, needsDocument: false);

            Registry.Add("new_document", "New drawing", ToolKind.App,
                "Create a new drawing from a template (default acadiso.dwt, metric) and make it active.",
                Obj(P("template", Str("Template file name or path, e.g. acadiso.dwt or acad.dwt"))), New, needsDocument: false);

            Registry.Add("save_document", "Save drawing", ToolKind.App,
                "Save the active drawing. No path: save in place. A .dwg path: Save As (the drawing is renamed). " +
                "A .dxf path: export a DXF copy (the open drawing keeps its name).",
                Obj(P("path", Str("Optional target path (.dwg or .dxf)"))), Save);

            Registry.Add("close_document", "Close drawing", ToolKind.App,
                "Close a drawing (default: the active one). Refuses when there are unsaved changes unless save=true or discard_changes=true.",
                Obj(P("document", Str("Drawing name or path (default active)")),
                    P("save", Bool("Save before closing")),
                    P("discard_changes", Bool("Close without saving: unsaved changes are lost"))),
                Close, destructive: true);

            Registry.Add("list_layouts", "List layouts", ToolKind.Read,
                "Model and paper space layouts (sheets) of the active drawing with tab order, viewport count, paper size and plotter.",
                Obj(), ListLayouts);

            Registry.Add("activate_layout", "Switch layout", ToolKind.Write,
                "Make a layout current (Model or a paper space layout name).",
                ObjR("name", P("name", Str("Layout name, or Model"))), ActivateLayout, tx: false);
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

        static object Open(ToolContext c, Args a)
        {
            string path = a.ReqStr("path");
            if (!File.Exists(path)) throw new ToolError($"File not found: {path}");
            Document existing = Find(Path.GetFullPath(path));
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
            Document d;
            if (!string.IsNullOrEmpty(template)) d = AcApp.DocumentManager.Add(template);
            else
            {
                try { d = AcApp.DocumentManager.Add("acadiso.dwt"); }
                catch (AcRx.Exception) { d = AcApp.DocumentManager.Add("acad.dwt"); }
            }
            return new Dictionary<string, object> { ["created"] = DocInfo(d) };
        }

        static object Save(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            string path = a.Str("path");
            if (path != null && path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
            {
                using (doc.LockDocument()) doc.Database.DxfOut(path, 16, DwgVersion.Current);
                return new Dictionary<string, object> { ["exported"] = path, ["format"] = "dxf" };
            }

            dynamic acadDoc = doc.GetAcadDocument();
            if (string.IsNullOrEmpty(path))
            {
                if (!doc.IsNamedDrawing) throw new ToolError("This drawing has never been saved: pass a .dwg path.");
                if (doc.IsReadOnly) throw new ToolError("The drawing is open read-only: pass a new .dwg path to save a copy.");
                acadDoc.Save();
            }
            else
            {
                acadDoc.SaveAs(path);
            }
            return new Dictionary<string, object> { ["saved"] = doc.Name };
        }

        static object Close(ToolContext c, Args a)
        {
            string name = a.Str("document");
            Document d = name == null ? c.Doc : Find(name) ?? throw new ToolError($"No open drawing matches '{name}'.");
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

        static object ListLayouts(ToolContext c, Args a)
        {
            var layouts = (DBDictionary)c.Tr.GetObject(c.Db.LayoutDictionaryId, OpenMode.ForRead);
            string current = LayoutManager.Current.CurrentLayout;
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
