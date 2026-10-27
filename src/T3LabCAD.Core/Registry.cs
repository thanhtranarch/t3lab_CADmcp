using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace T3LabCAD.Core
{
    internal enum ToolKind
    {
        /// <summary>Reads only. Runs even while a command is active; no document lock.</summary>
        Read,
        /// <summary>Changes the drawing: locks the document (one undo step), refused while a command is active.</summary>
        Write,
        /// <summary>Application level (documents, reload): no lock, no transaction; refused while a command is active.</summary>
        App,
    }

    internal delegate object ToolHandler(ToolContext ctx, Args args);

    internal sealed class ToolContext
    {
        public Document Doc;
        public Database Db;
        public Editor Ed;
        /// <summary>Open for Read/Write tools with Tx=true; committed when the handler returns, aborted if it throws.</summary>
        public Transaction Tr;
        public bool NeedsRegen;
    }

    internal sealed class ToolDef
    {
        public string Name, Title, Description;
        public Dictionary<string, object> Schema;
        public ToolKind Kind;
        public ToolHandler Handler;
        public bool NeedsDocument = true;
        public bool Tx = true;
        public bool Destructive;
        /// <summary>Handler runs on the socket thread and marshals its own steps (run_command).</summary>
        public bool Background;
        /// <summary>Runs even while a command is active (cancel_command, reload_addin).</summary>
        public bool DuringCommand;
    }

    /// <summary>Tool result rendered as an MCP image block plus a JSON text block.</summary>
    internal sealed class ImageResult
    {
        public string Base64;
        public string MimeType = "image/png";
        public object Info;
    }

    internal static class Registry
    {
        static Dictionary<string, ToolDef> _tools = new Dictionary<string, ToolDef>();

        public static int Count => _tools.Count;

        public static void Build()
        {
            _tools = new Dictionary<string, ToolDef>(StringComparer.Ordinal);
            StatusTools.Register();
            DocumentTools.Register();
            LayerTools.Register();
            QueryTools.Register();
            EditTools.Register();
            ViewTools.Register();
            CommandTools.Register();
        }

        public static void Add(string name, string title, ToolKind kind, string description, string schema, ToolHandler handler,
            bool needsDocument = true, bool tx = true, bool destructive = false, bool background = false, bool duringCommand = false)
        {
            _tools[name] = new ToolDef
            {
                Name = name,
                Title = title,
                Kind = kind,
                Description = description,
                Schema = Json.Schema(schema),
                Handler = handler,
                NeedsDocument = needsDocument,
                Tx = tx && kind != ToolKind.App,
                Destructive = destructive,
                Background = background,
                DuringCommand = duringCommand,
            };
        }

        public static ToolDef Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;

        public static object ListJson()
        {
            var tools = _tools.Values.Select(t => (object)new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["title"] = t.Title,
                ["description"] = t.Description,
                ["inputSchema"] = t.Schema,
                ["annotations"] = new Dictionary<string, object>
                {
                    ["title"] = t.Title,
                    ["readOnlyHint"] = t.Kind == ToolKind.Read,
                    ["destructiveHint"] = t.Destructive,
                    ["openWorldHint"] = false,
                },
            }).ToList();
            return new Dictionary<string, object> { ["tools"] = tools };
        }
    }

    /// <summary>Helpers to write JSON schemas compactly (single quotes, no quotes inside descriptions).</summary>
    internal static class Sch
    {
        public static string Obj(params string[] props) => ObjR("", props);

        /// <summary>Object schema; required is a comma-separated list of property names.</summary>
        public static string ObjR(string required, params string[] props)
        {
            string req = string.IsNullOrEmpty(required)
                ? ""
                : ",'required':[" + string.Join(",", required.Split(',').Select(r => "'" + r.Trim() + "'")) + "]";
            return "{'type':'object','properties':{" + string.Join(",", props) + "}" + req + "}";
        }

        public static string P(string name, string schema) => "'" + name + "':" + schema;
        public static string Str(string d) => "{'type':'string','description':'" + d + "'}";
        public static string Num(string d) => "{'type':'number','description':'" + d + "'}";
        public static string Int(string d) => "{'type':'integer','description':'" + d + "'}";
        public static string Bool(string d) => "{'type':'boolean','description':'" + d + "'}";
        public static string Any(string d) => "{'description':'" + d + "'}";
        public static string Strs(string d) => "{'type':'array','items':{'type':'string'},'description':'" + d + "'}";
        public static string Pt(string d) => "{'type':'array','items':{'type':'number'},'minItems':2,'maxItems':3,'description':'" + d + "'}";
        public static string Pts(string d) => "{'type':'array','items':{'type':'array','items':{'type':'number'}},'description':'" + d + "'}";
        public static string Enum(string d, params string[] values) =>
            "{'type':'string','enum':[" + string.Join(",", values.Select(v => "'" + v + "'")) + "],'description':'" + d + "'}";
        public static string ObjAny(string d) => "{'type':'object','description':'" + d + "'}";
        public static string Arr(string d, string itemSchema) => "{'type':'array','items':" + itemSchema + ",'description':'" + d + "'}";

        public const string Space = "'space':{'type':'string','description':'model, paper, current (default) or a layout name'}";
        public const string Handles = "'handles':{'type':'array','items':{'type':'string'},'description':'Entity handles (hex strings from query_entities)'}";
    }
}
