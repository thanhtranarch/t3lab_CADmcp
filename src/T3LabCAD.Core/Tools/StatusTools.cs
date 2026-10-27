using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcRx = Autodesk.AutoCAD.Runtime;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    internal static class StatusTools
    {
        public static void Register()
        {
            Registry.Add("get_status", "AutoCAD status", ToolKind.Read,
                "Start here. AutoCAD version, open drawings, and for the active drawing: units, current layer/layout/space, " +
                "entity counts in model space, drawing extents and whether a command is running.",
                Obj(), GetStatus, needsDocument: false, tx: false);

            Registry.Add("get_sysvars", "Read system variables", ToolKind.Read,
                "Read AutoCAD system variables, e.g. INSUNITS, OSMODE, LTSCALE, DIMSCALE, CANNOSCALE, CLAYER.",
                ObjR("names", P("names", Strs("System variable names"))), GetSysvars, tx: false);

            Registry.Add("set_sysvar", "Set a system variable", ToolKind.Write,
                "Set one AutoCAD system variable. The value is converted to the variable type (integer, real, string or point).",
                ObjR("name,value", P("name", Str("System variable name")), P("value", Any("New value"))), SetSysvar, tx: false);
        }

        static object GetStatus(ToolContext c, Args a)
        {
            DocumentCollection dm = AcApp.DocumentManager;
            Document active = dm.MdiActiveDocument;
            var docs = new List<object>();
            foreach (Document d in dm)
            {
                docs.Add(new Dictionary<string, object>
                {
                    ["name"] = Path.GetFileName(d.Name),
                    ["path"] = d.Name,
                    ["active"] = d == active,
                    ["read_only"] = d.IsReadOnly,
                    ["saved_to_file"] = d.IsNamedDrawing,
                });
            }

            var result = new Dictionary<string, object>
            {
                ["addin"] = "T3LabCAD " + Entry.Version,
                ["port"] = Entry.Port,
                ["autocad"] = Convert.ToString(AcApp.GetSystemVariable("ACADVER")),
                ["product"] = Convert.ToString(AcApp.GetSystemVariable("PRODUCT")),
                ["documents"] = docs,
            };
            if (active == null) return result;

            Database db = active.Database;
            var info = new Dictionary<string, object>
            {
                ["name"] = Path.GetFileName(active.Name),
                ["modified"] = Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0,
                ["command_in_progress"] = active.CommandInProgress,
                ["insunits"] = db.Insunits.ToString(),
                ["measurement"] = db.Measurement.ToString(),
                ["current_layout"] = LayoutManager.Current.CurrentLayout,
                ["textsize"] = Geo.R(db.Textsize),
                ["ltscale"] = Geo.R(db.Ltscale),
                ["dimscale"] = Geo.R(db.Dimscale),
            };

            using (var tr = db.TransactionManager.StartTransaction())
            {
                info["current_layer"] = ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name;
                info["current_textstyle"] = ((TextStyleTableRecord)tr.GetObject(db.Textstyle, OpenMode.ForRead)).Name;
                info["current_dimstyle"] = ((DimStyleTableRecord)tr.GetObject(db.Dimstyle, OpenMode.ForRead)).Name;
                ObjectId ms = SymbolUtilityServices.GetBlockModelSpaceId(db);
                info["current_space"] = db.TileMode ? "model" : db.CurrentSpaceId == ms ? "model (through a viewport)" : "paper";

                var counts = new Dictionary<string, int>();
                int total = 0;
                foreach (ObjectId id in (BlockTableRecord)tr.GetObject(ms, OpenMode.ForRead))
                {
                    string type = id.ObjectClass.DxfName;
                    counts[type] = counts.TryGetValue(type, out int n) ? n + 1 : 1;
                    total++;
                }
                info["model_space_entities"] = total;
                info["model_space_by_type"] = counts.OrderByDescending(kv => kv.Value).Take(20)
                    .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                tr.Commit();
            }

            Point3d min = db.Extmin, max = db.Extmax;
            if (min.X <= max.X && min.Y <= max.Y) info["model_extents"] = new[] { Geo.P(min), Geo.P(max) };
            result["active_document"] = info;
            return result;
        }

        static object GetSysvars(ToolContext c, Args a)
        {
            var values = new Dictionary<string, object>();
            foreach (string name in a.Strs("names"))
            {
                try { values[name.ToUpperInvariant()] = SysvarJson(AcApp.GetSystemVariable(name)); }
                catch (AcRx.Exception) { values[name.ToUpperInvariant()] = "(unknown variable)"; }
            }
            return values;
        }

        static object SetSysvar(ToolContext c, Args a)
        {
            string name = a.ReqStr("name");
            object current;
            try { current = AcApp.GetSystemVariable(name); }
            catch (AcRx.Exception) { throw new ToolError($"Unknown system variable '{name}'."); }

            object raw = a.Raw("value");
            object value;
            if (current is short) value = Convert.ToInt16(Args.ToDouble(raw, "value"));
            else if (current is int) value = Convert.ToInt32(Args.ToDouble(raw, "value"));
            else if (current is double) value = Args.ToDouble(raw, "value");
            else if (current is Point3d) value = Args.ToPoint(raw, "value");
            else if (current is Point2d) { var p = Args.ToPoint(raw, "value"); value = new Point2d(p.X, p.Y); }
            else value = Convert.ToString(raw);

            try { AcApp.SetSystemVariable(name, value); }
            catch (AcRx.Exception ex) { throw new ToolError($"AutoCAD refused {name.ToUpperInvariant()} = {raw}: {ex.ErrorStatus} (read-only or out of range)."); }
            return new Dictionary<string, object>
            {
                ["name"] = name.ToUpperInvariant(),
                ["old"] = SysvarJson(current),
                ["new"] = SysvarJson(AcApp.GetSystemVariable(name)),
            };
        }

        static object SysvarJson(object v)
        {
            switch (v)
            {
                case Point3d p3: return Geo.P(p3);
                case Point2d p2: return Geo.P(p2);
                case double d: return Geo.R(d);
                case short _:
                case int _:
                case string _:
                case bool _:
                    return v;
                default: return Convert.ToString(v);
            }
        }
    }
}
