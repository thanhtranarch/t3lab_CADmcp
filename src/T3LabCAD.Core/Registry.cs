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

    /// <summary>
    /// Which drawings a tool can work in. Any: the active one, another open one, or one opened in the background (a database
    /// with no window: open_document background=true), pinned with autocad_document. Open: open drawings only (it needs the
    /// Document). Active: the active drawing only (the editor, views, system variables, layouts, the command line).
    /// scripts/check_tool_scopes.py proves that no tool reaches more than it declares.
    /// </summary>
    internal enum ToolScope { Any, Open, Active }

    internal delegate object ToolHandler(ToolContext ctx, Args args);

    internal sealed class ToolContext
    {
        /// <summary>Null for a background drawing (a database only).</summary>
        public Document Doc;
        public Database Db;
        /// <summary>Null unless the call works in the active drawing.</summary>
        public Editor Ed;
        /// <summary>Open for Read/Write tools with Tx=true; committed when the handler returns, aborted if it throws.</summary>
        public Transaction Tr;
        public bool NeedsRegen;
        /// <summary>The drawing's file (full path), or null when it was never saved.</summary>
        public string FilePath;

        /// <summary>The same drawing with another transaction (a sub-step that may fail on its own).</summary>
        public ToolContext With(Transaction tr) => new ToolContext { Doc = Doc, Db = Db, Ed = Ed, Tr = tr, FilePath = FilePath };

        /// <summary>The editor, for a branch of a tool that needs it; elsewhere a clear refusal, never a null.</summary>
        public Editor RequireEditor(string what) =>
            Ed ?? throw new NeedsActiveException($"{what} needs the active drawing: '{System.IO.Path.GetFileName(FilePath ?? "this drawing")}' is not active in AutoCAD.");
    }

    /// <summary>A pinned call reached something only the active drawing has; nothing was changed. Rpc answers needs_active_document.</summary>
    internal sealed class NeedsActiveException : Exception
    {
        public NeedsActiveException(string message) : base(message) { }
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
        /// <summary>A Read tool that can also write a file (CSV export): not advertised as read-only.</summary>
        public bool WritesFiles;
        public ToolScope Scope;
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
            AgentCursors.Register();
            DocumentTools.Register();
            FileTools.Register();
            LayoutTools.Register();
            ExportTools.Register();
            SheetTools.Register();
            LayerTools.Register();
            StyleTools.Register();
            NamedObjectTools.Register();
            ImportTools.Register();
            QueryTools.Register();
            MeasureTools.Register();
            EditTools.Register();
            ModifyTools.Register();
            FieldTools.Register();
            XrefTools.Register();
            ReferenceTools.Register();
            CivilTools.Register();
            ViewTools.Register();
            CommandTools.Register();
        }

        public static void Add(string name, string title, ToolKind kind, string description, string schema, ToolHandler handler,
            bool needsDocument = true, bool tx = true, bool destructive = false, bool background = false, bool duringCommand = false,
            bool writesFiles = false, ToolScope scope = ToolScope.Any)
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
                WritesFiles = writesFiles,
                Scope = scope,
            };
        }

        /// <summary>The argument that sends a call to another drawing (stripped before the tool runs, Rpc.Execute).</summary>
        public const string DocumentPin = "autocad_document";

        const string PinAny =
            "Drawing to work in: name or path of an open drawing, or of one opened in the background (open_document background=true). " +
            "Default: the active drawing. It does not switch the drawing the user sees";
        const string PinOpen =
            "Drawing to work in: name or path of a drawing open in AutoCAD (not a background one). Default: the active drawing. " +
            "It does not switch the drawing the user sees";

        /// <summary>Tools that can work in another drawing advertise the pin (scripts/gen_tools_snapshot.py adds the same).</summary>
        public static bool TakesPin(ToolDef t) => t.NeedsDocument && t.Scope != ToolScope.Active;

        /// <summary>A copy of the schema with the autocad_document property (the stored schema is shared, never changed).</summary>
        static Dictionary<string, object> WithPin(ToolDef t)
        {
            var copy = new Dictionary<string, object>(t.Schema);
            var props = t.Schema.TryGetValue("properties", out object p) && p is Dictionary<string, object> d
                ? new Dictionary<string, object>(d) : new Dictionary<string, object>();
            props[DocumentPin] = new Dictionary<string, object> { ["type"] = "string", ["description"] = t.Scope == ToolScope.Open ? PinOpen : PinAny };
            copy["properties"] = props;
            return copy;
        }

        public static ToolDef Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;

        public static object ListJson()
        {
            var tools = _tools.Values.Select(t => (object)new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["title"] = t.Title,
                ["description"] = t.Description,
                ["inputSchema"] = TakesPin(t) ? WithPin(t) : t.Schema,
                ["annotations"] = new Dictionary<string, object>
                {
                    ["title"] = t.Title,
                    ["readOnlyHint"] = t.Kind == ToolKind.Read && !t.WritesFiles,
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
        public static string EnumArr(string d, params string[] values) =>
            "{'type':'array','items':{'type':'string','enum':[" + string.Join(",", values.Select(v => "'" + v + "'")) + "]},'description':'" + d + "'}";

        public const string Space = "'space':{'type':'string','description':'model, paper, current (default), a layout name, or block:NAME to work inside a block definition'}";
        public const string Handles = "'handles':{'type':'array','items':{'type':'string'},'description':'Entity handles (hex strings from query_entities)'}";
    }
}
