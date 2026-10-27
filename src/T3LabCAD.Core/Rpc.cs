using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace T3LabCAD.Core
{
    /// <summary>Handles one request line from the bridge. Methods: health, tools/list, tools/call.</summary>
    internal static class Rpc
    {
        public static string Handle(string line)
        {
            Dictionary<string, object> request;
            try { request = Json.Parse(line) as Dictionary<string, object>; }
            catch { return null; }
            if (request == null || !request.ContainsKey("method")) return null;

            request.TryGetValue("id", out object id);
            string method = Convert.ToString(request["method"]);
            request.TryGetValue("params", out object rawParams);
            var p = new Args(rawParams);

            try
            {
                object result;
                switch (method)
                {
                    case "health": result = Health(); break;
                    case "tools/list": result = Registry.ListJson(); break;
                    case "tools/call": result = CallTool(p.ReqStr("name"), new Args(p.Raw("arguments"))); break;
                    default: return Error(id, -32601, "Unknown method: " + method);
                }
                return Json.Write(new Dictionary<string, object> { ["id"] = id, ["result"] = result });
            }
            catch (Exception ex)
            {
                Log.Error(method + ": " + ex);
                return Error(id, -32603, ex.Message);
            }
        }

        static string Error(object id, int code, string message) =>
            Json.Write(new Dictionary<string, object>
            {
                ["id"] = id,
                ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message },
            });

        /// <summary>Answered on the socket thread (never waits for AutoCAD), so the bridge can probe busy instances.</summary>
        static object Health() => new Dictionary<string, object>
        {
            ["app"] = "T3LabCAD",
            ["version"] = Entry.Version,
            ["pid"] = Process.GetCurrentProcess().Id,
            ["port"] = Entry.Port,
            ["autocad"] = Entry.AcadVersion,
            ["active_document"] = Entry.ActiveDocument,
            ["calls"] = Interlocked.Read(ref Entry.CallCount),
        };

        static object CallTool(string name, Args args)
        {
            ToolDef def = Registry.Get(name);
            if (def == null) return ErrorResult($"Unknown tool '{name}'.");
            Interlocked.Increment(ref Entry.CallCount);
            var sw = Stopwatch.StartNew();
            string outcome = "ok";
            try
            {
                object value = def.Background
                    ? def.Handler(null, args)
                    : Entry.Main.Run(() => Execute(def, args), applicationContext: def.Kind == ToolKind.App);
                return Success(value);
            }
            catch (ToolError te)
            {
                outcome = "error: " + te.Message;
                return ErrorResult(te.Message);
            }
            catch (AcRx.Exception ae)
            {
                outcome = "autocad error: " + ae.ErrorStatus;
                string msg = ae.Message == ae.ErrorStatus.ToString() ? "" : ": " + ae.Message;
                return ErrorResult($"AutoCAD error {ae.ErrorStatus}{msg}");
            }
            catch (Exception ex)
            {
                outcome = "exception: " + ex.GetType().Name;
                Log.Error(name + ": " + ex);
                return ErrorResult($"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Entry.LastCall = $"{name} ({sw.ElapsedMilliseconds} ms)";
                Log.Info($"{name} {sw.ElapsedMilliseconds}ms {outcome}");
            }
        }

        static object Execute(ToolDef def, Args args)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (def.NeedsDocument && doc == null)
                throw new ToolError("No drawing is open in AutoCAD. Use open_document or new_document first.");
            if (def.Kind != ToolKind.Read && !def.DuringCommand && doc != null)
            {
                string cip = doc.CommandInProgress;
                if (!string.IsNullOrEmpty(cip))
                    throw new ToolError($"AutoCAD is busy with command '{cip}'. Ask the user to finish it or press Esc, then retry.");
            }

            var ctx = new ToolContext { Doc = doc, Db = doc?.Database, Ed = doc?.Editor };
            DocumentLock docLock = def.Kind == ToolKind.Write && doc != null ? doc.LockDocument() : null;
            try
            {
                object result;
                if (def.Tx && doc != null)
                {
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        ctx.Tr = tr;
                        result = def.Handler(ctx, args);
                        tr.Commit();
                    }
                }
                else
                {
                    result = def.Handler(ctx, args);
                }

                if (def.Kind == ToolKind.Write && doc != null)
                {
                    if (ctx.NeedsRegen) doc.Editor.Regen();
                    AcApp.UpdateScreen();
                }
                return result;
            }
            finally
            {
                docLock?.Dispose();
            }
        }

        static object Success(object value)
        {
            if (value is ImageResult img)
            {
                return new Dictionary<string, object>
                {
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object> { ["type"] = "image", ["data"] = img.Base64, ["mimeType"] = img.MimeType },
                        new Dictionary<string, object> { ["type"] = "text", ["text"] = Json.Write(img.Info) },
                    },
                };
            }
            return new Dictionary<string, object>
            {
                ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = Json.Write(value) } },
            };
        }

        static object ErrorResult(string message) => new Dictionary<string, object>
        {
            ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = message } },
            ["isError"] = true,
        };
    }
}
