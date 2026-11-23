using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>Named page setups and layer states.</summary>
    internal static class SheetTools
    {
        public static void Register()
        {
            Registry.Add("set_page_setup", "Create or edit page setup", ToolKind.Write,
                "Create a named page setup (if missing) and/or change it: plotter, paper (A3, A1, a canonical name...), plot_style, area, " +
                "window, scale (fit or 1:100), center, rotation; then apply_to layouts (* = every paper layout). A new one starts as a copy " +
                "of from_layout (default the current layout). List / rename / delete with the named-object tools, kind=page_setup.",
                ObjR("name",
                    P("name", Str("Page setup name")),
                    P("create", Bool("Create it when missing (default true)")),
                    P("from_layout", Str("New page setup: copy the settings of this layout (default current; Model for a model page setup)")),
                    P("plotter", Str("Plotter .pc3 or printer name")),
                    P("paper", Str("Paper size: canonical name or a part of it, e.g. A3")),
                    P("plot_style", Str("Plot style table, e.g. monochrome.ctb")),
                    P("area", Enum("What to plot", "layout", "extents", "display", "limits", "window")),
                    P("window", Pts("area=window: [[x1, y1], [x2, y2]]")),
                    P("scale", Any("fit, or paper:drawing units like 1:100")),
                    P("center", Bool("Center on the paper")),
                    P("rotation", Enum("Plot rotation in degrees", "0", "90", "180", "270")),
                    P("apply_to", Strs("Layouts that take these settings, * = every paper layout"))),
                SetPageSetup);

            Registry.Add("layer_state", "Layer states", ToolKind.Write,
                "Layer states (LAYERSTATE): list, save the current layer settings under a name (replace=true overwrites), restore one, " +
                "delete, rename, export to a .las file, import from a .las file.",
                ObjR("action",
                    P("action", Enum("What to do", "list", "save", "restore", "delete", "rename", "export", "import")),
                    P("name", Str("Layer state name")),
                    P("new_name", Str("rename: new name")),
                    P("description", Str("save: description")),
                    P("replace", Bool("save: overwrite an existing state (default false)")),
                    P("file", Str("export / import: .las file path"))),
                LayerState, tx: false);
        }

        // ---------------------------------------------------------------- page setups

        internal static Dictionary<string, object> PageSetupInfo(PlotSettings ps)
        {
            var d = ExportTools.Describe(ps.PlotSettingsName, ps);
            d.Remove("layout");
            d["name"] = ps.PlotSettingsName;
            d["model"] = ps.ModelType;
            return d;
        }

        static object SetPageSetup(ToolContext c, Args a)
        {
            var db = c.Db;
            var tr = c.Tr;
            string name = a.ReqStr("name").Trim();
            var dict = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
            LayoutManager lm = LayoutManager.Current;
            PlotSettings ps;
            bool created = false;
            if (dict.Contains(name)) ps = (PlotSettings)tr.GetObject(dict.GetAt(name), OpenMode.ForWrite);
            else
            {
                if (!a.Bool("create", true)) throw new ToolError($"No page setup named '{name}'.");
                Acad.ValidateName(name, "page setup");
                string from = a.Str("from_layout") ?? lm.CurrentLayout;
                if (!lm.LayoutExists(from)) throw new ToolError($"Layout '{from}' does not exist (see list_layouts).");
                var source = (Layout)tr.GetObject(lm.GetLayoutId(from), OpenMode.ForRead);
                ps = new PlotSettings(source.ModelType);
                ps.CopyFrom(source);
                ps.PlotSettingsName = name;
                created = true;
            }

            string device = a.Str("plotter") ?? ps.PlotConfigurationName;
            ExportTools.Configure(ps, ps, a, device, false);
            if (created)
            {
                ps.AddToPlotSettingsDictionary(db);
                tr.AddNewlyCreatedDBObject(ps, true);
            }

            var applied = new List<object>();
            if (a.Has("apply_to"))
            {
                List<string> patterns = a.Strs("apply_to");
                foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead))
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    bool wildcard = patterns.Any(p => p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0);
                    if (!Wild.Any(layout.LayoutName, patterns)) continue;
                    if (layout.ModelType != ps.ModelType)
                    {
                        if (wildcard) continue; // * means the layouts of the same kind
                        throw new ToolError($"'{layout.LayoutName}' is a {(layout.ModelType ? "model" : "paper")} layout and '{name}' a " +
                                            $"{(ps.ModelType ? "model" : "paper")} page setup.");
                    }
                    layout.UpgradeOpen();
                    layout.CopyFrom(ps);
                    applied.Add(layout.LayoutName);
                }
                if (applied.Count == 0) throw new ToolError("No layout of the right kind matches apply_to (see list_layouts).");
                c.NeedsRegen = true;
            }
            return new Dictionary<string, object>
            {
                ["page_setup"] = PageSetupInfo(ps),
                ["created"] = created,
                ["applied_to"] = applied,
            };
        }

        // ---------------------------------------------------------------- layer states

        const LayerStateMasks AllMasks =
            LayerStateMasks.On | LayerStateMasks.Frozen | LayerStateMasks.Locked | LayerStateMasks.Plot | LayerStateMasks.NewViewport |
            LayerStateMasks.Color | LayerStateMasks.LineType | LayerStateMasks.LineWeight | LayerStateMasks.PlotStyle;

        static List<string> Names(LayerStateManager lsm) =>
            (lsm.GetLayerStateNames(false, false) ?? new System.Collections.ArrayList()).Cast<object>().Select(o => Convert.ToString(o)).ToList();

        static object LayerState(ToolContext c, Args a)
        {
            LayerStateManager lsm = c.Db.LayerStateManager;
            string action = a.ReqStr("action").Trim().ToLowerInvariant();
            string name = a.Str("name")?.Trim();
            string RequireName() => string.IsNullOrEmpty(name) ? throw new ToolError($"{action} needs name.") : name;
            void RequireExisting()
            {
                if (!lsm.HasLayerState(RequireName())) throw new ToolError($"No layer state named '{name}' (action=list shows them).");
            }

            switch (action)
            {
                case "list":
                {
                    var states = new List<object>();
                    foreach (string n in Names(lsm))
                    {
                        var item = new Dictionary<string, object> { ["name"] = n };
                        try
                        {
                            string description = lsm.GetLayerStateDescription(n);
                            if (!string.IsNullOrEmpty(description)) item["description"] = description;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception) { /* no description */ }
                        states.Add(item);
                    }
                    return new Dictionary<string, object> { ["count"] = states.Count, ["layer_states"] = states, ["last_restored"] = lsm.LastRestoredLayerState };
                }
                case "save":
                    RequireName();
                    if (lsm.HasLayerState(name))
                    {
                        if (!a.Bool("replace", false)) throw new ToolError($"Layer state '{name}' exists: pass replace=true to overwrite it.");
                        lsm.DeleteLayerState(name);
                    }
                    lsm.SaveLayerState(name, AllMasks, ObjectId.Null);
                    if (a.Has("description")) lsm.SetLayerStateDescription(name, a.Str("description") ?? "");
                    return new Dictionary<string, object> { ["saved"] = name };
                case "restore":
                    RequireExisting();
                    lsm.RestoreLayerState(name, ObjectId.Null, 0, AllMasks);
                    c.NeedsRegen = true;
                    return new Dictionary<string, object> { ["restored"] = name };
                case "delete":
                    RequireExisting();
                    lsm.DeleteLayerState(name);
                    return new Dictionary<string, object> { ["deleted"] = name };
                case "rename":
                {
                    RequireExisting();
                    string newName = a.ReqStr("new_name").Trim();
                    if (lsm.HasLayerState(newName)) throw new ToolError($"A layer state named '{newName}' already exists.");
                    lsm.RenameLayerState(name, newName);
                    return new Dictionary<string, object> { ["renamed"] = name, ["to"] = newName };
                }
                case "export":
                {
                    RequireExisting();
                    string file = DocumentTools.ResolvePath(a.ReqStr("file"), ".las");
                    if (!Directory.Exists(Path.GetDirectoryName(file))) throw new ToolError($"Folder not found: {Path.GetDirectoryName(file)}");
                    lsm.ExportLayerState(name, file);
                    return new Dictionary<string, object> { ["exported"] = name, ["file"] = file };
                }
                case "import":
                {
                    string file = DocumentTools.ResolvePath(a.ReqStr("file"), ".las");
                    if (!File.Exists(file)) throw new ToolError($"File not found: {file}");
                    var before = new HashSet<string>(Names(lsm), StringComparer.OrdinalIgnoreCase);
                    lsm.ImportLayerState(file);
                    var added = Names(lsm).Where(n => !before.Contains(n)).Cast<object>().ToList();
                    return new Dictionary<string, object> { ["imported_from"] = file, ["new_layer_states"] = added };
                }
                default:
                    throw new ToolError("action must be list, save, restore, delete, rename, export or import.");
            }
        }
    }
}
