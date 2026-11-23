using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>Fields: add_entities text / mtext with a field {...}; update_fields refreshes them (UPDATEFIELD).</summary>
    internal static class FieldTools
    {
        /// <summary>Drawing-level field types and their AcVar names.</summary>
        static readonly Dictionary<string, string> Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["filename"] = "Filename",
            ["date"] = "Date",
            ["savedate"] = "SaveDate",
            ["plotdate"] = "PlotDate",
            ["author"] = "Author",
            ["title"] = "Title",
            ["subject"] = "Subject",
            ["keywords"] = "Keywords",
        };

        public static void Register()
        {
            Registry.Add("update_fields", "Update fields", ToolKind.Write,
                "Re-evaluate the fields of the active drawing, like UPDATEFIELD on everything (object areas and lengths, dates, file name). " +
                "Make fields with add_entities text / mtext field {...}.",
                Obj(), UpdateFields);
        }

        static object UpdateFields(ToolContext c, Args a)
        {
            c.Db.EvaluateFields();
            c.NeedsRegen = true;
            return new Dictionary<string, object> { ["updated"] = true };
        }

        /// <summary>
        /// The field code for field {type, handle, property, format, factor, precision, prefix, suffix, code}:
        /// a drawing variable (filename, date...), an object property (area, length, or property + handle), or raw code.
        /// </summary>
        public static string Code(ToolContext c, Args f)
        {
            string type = (f.Str("type") ?? (f.Has("code") ? "code" : "")).Trim().ToLowerInvariant();
            string format = f.Str("format");
            string inner;
            if (type == "code")
            {
                inner = f.ReqStr("code");
                if (!inner.Contains("%<")) throw new ToolError("field code must look like %<\\AcVar Date>% (with %< and >%).");
            }
            else if (Variables.TryGetValue(type, out string variable))
            {
                if (format == null && type.EndsWith("date", StringComparison.Ordinal)) format = "dd/MM/yyyy";
                inner = "%<\\AcVar " + variable + (format != null ? " \\f \"" + format + "\"" : "") + ">%";
            }
            else if (type == "area" || type == "length" || type == "object")
            {
                ObjectId id = Acad.Id(c.Db, f.ReqStr("handle"));
                string property = type == "area" ? "Area" : type == "length" ? "Length" : f.ReqStr("property");
                if (format == null && (type == "area" || type == "length"))
                {
                    format = "%lu2%pr" + Math.Max(0, Math.Min(8, f.Int("precision", 2)));
                    if (f.Has("factor")) format += "%ct8[" + f.ReqNum("factor").ToString("R", CultureInfo.InvariantCulture) + "]";
                }
                inner = "%<\\AcObjProp Object(%<\\_ObjId " + id.OldIdPtr.ToInt64().ToString(CultureInfo.InvariantCulture) + ">%)." + property +
                        (format != null ? " \\f \"" + format + "\"" : "") + ">%";
            }
            else
            {
                throw new ToolError("field type must be filename, date, savedate, plotdate, author, title, subject, keywords, area, length, object or code.");
            }
            return (f.Str("prefix") ?? "") + inner + (f.Str("suffix") ?? "");
        }

        /// <summary>Put a field on a text / mtext that is already in the drawing.</summary>
        public static void Attach(ToolContext c, Entity e, object raw)
        {
            var f = new Args(raw);
            var field = new Field(Code(c, f));
            try
            {
                field.Evaluate();
                switch (e)
                {
                    case MText m: m.SetField(field); break;
                    case DBText t: t.SetField(field); break;
                    default: throw new ToolError("fields go on text or mtext.");
                }
                c.Tr.AddNewlyCreatedDBObject(field, true);
            }
            catch
            {
                if (field.ObjectId.IsNull) field.Dispose();
                throw;
            }
        }
    }
}
