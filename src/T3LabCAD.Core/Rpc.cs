using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
                    case "tools/call":
                        result = CallTool(p.ReqStr("name"), new Args(p.Raw("arguments")), AgentCursors.ClientKey(p.Raw("client")),
                            p.Str("lane"), p.Num("queue_wait_s", 0));
                        break;
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
            ["queue"] = QueueStatus(),
        };

        /// <summary>Conductor status with display names ("Claude Code"), never the bridge session ids.</summary>
        static Dictionary<string, object> QueueStatus()
        {
            Dictionary<string, object> q = Entry.Queue.Status();
            if (q.TryGetValue("agent", out object a)) q["agent"] = AgentCursors.Label(a as string);
            if (q.TryGetValue("waiting_agents", out object w) && w is List<object> list)
                q["waiting_agents"] = list.Select(x => (object)AgentCursors.Label(x as string)).Distinct().ToList();
            return q;
        }

        /// <summary>No deadline from the bridge (older bridge, direct client): a call waits at most this long for its turn.</summary>
        const int DefaultQueueWaitMs = 60000, MaxQueueWaitMs = 30 * 60 * 1000;

        static object CallTool(string name, Args args, string client, string lane, double queueWaitS)
        {
            ToolDef def = Registry.Get(name);
            if (def == null) return ErrorResult($"Unknown tool '{name}'.");
            Interlocked.Increment(ref Entry.CallCount);
            var sw = Stopwatch.StartNew();
            string outcome = "ok";
            // Agent Cursors only records here; it can never fail the call
            AgentCursors.CurrentAgent = client;
            AgentStep step = null;
            try { step = AgentCursors.Begin(def, args, client); }
            catch (Exception ex) { Log.Error("agent cursors: " + ex.Message); }
            bool ok = false, refused = false, busy = false;
            string error = null;
            object value = null;
            int deadlineMs = queueWaitS > 0 ? (int)Math.Min(queueWaitS * 1000, MaxQueueWaitMs) : DefaultQueueWaitMs;
            // one call at a time, in a fair order (Conductor); t3lab_narrate and cancel_command never wait in line
            Conductor.Ticket ticket = null;
            try
            {
                if (AgentCursors.Blocks(def, args))
                {
                    refused = true;
                    throw new ToolError(AgentCursors.PausedMessage);
                }
                bool capture = step != null && AgentCursors.Captures(def);
                if (def.Name != AgentCursors.NarrateTool && !def.DuringCommand)
                {
                    ticket = Entry.Queue.Enter(client, name, lane, deadlineMs, (ahead, running) => AgentCursors.Queued(step, ahead, running));
                    AgentCursors.Granted(step);
                    if (def.Background) { ticket.MarkRunning(); AgentCursors.OnMain(step); }
                }
                if (def.Background)
                {
                    AgentCursors.Capture watch = capture ? OnMainOrNull(() => StartCapture(step)) : null;
                    try { value = def.Handler(null, args); }
                    finally { if (watch != null) EndCapture(watch); }
                }
                else
                {
                    MainThread main = Entry.Main ?? throw new ToolError("The T3LabCAD server is stopping. Nothing was run; retry in a moment.");
                    // AutoCAD must start it within what is left of the deadline, or it never runs (no late "ghost write")
                    int startMs = Math.Max(5000, deadlineMs - (int)sw.ElapsedMilliseconds);
                    value = main.Run(() =>
                    {
                        ticket?.MarkRunning();
                        AgentCursors.OnMain(step);
                        // Pause pressed while this call waited for the main thread
                        if (AgentCursors.Blocks(def, args))
                        {
                            refused = true;
                            throw new ToolError(AgentCursors.PausedMessage);
                        }
                        AgentCursors.Capture watch = null;
                        if (capture)
                        {
                            try { watch = StartCapture(step); }
                            catch (Exception ex) { Log.Error("agent cursors capture: " + ex.Message); }
                        }
                        try { return Execute(def, args); }
                        finally { watch?.Dispose(); }
                    }, startMs, applicationContext: def.Kind == ToolKind.App);
                }
                ok = true;
                return Success(value);
            }
            catch (Conductor.BusyException be)
            {
                busy = true;
                outcome = "busy: " + be.Message;
                error = "Not run, AutoCAD was busy: " + be.Message;
                return BusyResult(be, deadlineMs / 1000.0);
            }
            catch (MainThread.NotStartedException ns) when (!def.Background)
            {
                busy = true;
                outcome = "not started: " + ns.Message;
                error = ns.Message;
                return Coded("autocad_busy", ns.Message, true,
                    "Ask the user to close the dialog or finish what is open in AutoCAD, then retry.", 10);
            }
            catch (MainThread.NotStartedException ns)
            {
                // a background tool (run_command) may have done part of its work already: no nothing_ran claim
                outcome = "not started: " + ns.Message;
                error = ns.Message;
                return ErrorResult(ns.Message);
            }
            catch (ToolError te) when (refused)
            {
                outcome = "paused";
                error = te.Message;
                return Coded("paused_by_user", te.Message, true,
                    "Do read-only work meanwhile, or ask the user to press Resume on the T3Lab Agent Cursors bar (T3CURSORS).");
            }
            catch (ToolError te)
            {
                outcome = "error: " + te.Message;
                error = te.Message;
                return ErrorResult(te.Message);
            }
            catch (AcRx.Exception ae)
            {
                outcome = "autocad error: " + ae.ErrorStatus;
                string msg = ae.Message == ae.ErrorStatus.ToString() ? "" : ": " + ae.Message;
                error = $"AutoCAD error {ae.ErrorStatus}{msg}";
                return ErrorResult(error);
            }
            catch (Exception ex)
            {
                outcome = "exception: " + ex.GetType().Name;
                Log.Error(name + ": " + ex);
                error = $"{ex.GetType().Name}: {ex.Message}";
                return ErrorResult(error);
            }
            finally
            {
                ticket?.Dispose();
                Entry.LastCall = $"{name} ({sw.ElapsedMilliseconds} ms)";
                Log.Info($"{name} {sw.ElapsedMilliseconds}ms {outcome}");
                try { AgentCursors.End(step, ok, refused, busy, error, value); }
                catch (Exception ex) { Log.Error("agent cursors: " + ex.Message); }
            }
        }

        /// <summary>A call that never got its turn: what holds AutoCAD, and when to try again. Nothing ran.</summary>
        static object BusyResult(Conductor.BusyException be, double waitedS)
        {
            string who = be.Agent == null ? null : AgentCursors.Label(be.Agent);
            string message = be.Agent == null
                ? $"AutoCAD did not get to this call within {waitedS:0} s. Nothing was run."
                : be.Started
                    ? $"AutoCAD is busy with another AI call ({be.Tool} for {who}, {be.RunningS:0} s so far). Nothing was run."
                    : $"AutoCAD has not started the call ahead of this one ({be.Tool} for {who}): a modal dialog or a long " +
                      "operation may be blocking AutoCAD. Nothing was run.";
            var d = new Dictionary<string, object>
            {
                ["error_code"] = "autocad_busy",
                ["message"] = message,
                ["retryable"] = true,
                ["nothing_ran"] = true,
                ["retry_after_s"] = Math.Max(5, Math.Min(30, 5 + be.Ahead * 5)),
                ["ahead"] = be.Ahead,
                ["hint"] = be.Started || be.Agent == null
                    ? "Do work that does not need AutoCAD meanwhile (plan, read results you have), then retry after retry_after_s."
                    : "Ask the user to close the dialog or finish what is open in AutoCAD, then retry.",
            };
            if (be.Agent != null)
                d["running"] = new Dictionary<string, object>
                {
                    ["agent"] = who, ["tool"] = be.Tool, ["running_s"] = be.RunningS, ["state"] = be.Started ? "running" : "waiting_for_autocad",
                };
            return ErrorResult(Json.Write(d));
        }

        /// <summary>A refusal an agent can act on: {error_code, message, retryable, nothing_ran, hint}.</summary>
        static object Coded(string code, string message, bool retryable, string hint, int retryAfterS = 0)
        {
            var d = new Dictionary<string, object>
            {
                ["error_code"] = code,
                ["message"] = message,
                ["retryable"] = retryable,
                ["nothing_ran"] = true,
                ["hint"] = hint,
            };
            if (retryAfterS > 0) d["retry_after_s"] = retryAfterS;
            return ErrorResult(Json.Write(d));
        }

        /// <summary>Agent Cursors set-up for a background tool: on the main thread, but never fails or delays the call much.</summary>
        static T OnMainOrNull<T>(Func<T> fn) where T : class
        {
            try { return Entry.Main.Run(fn, 5000); }
            catch (Exception ex)
            {
                Log.Error("agent cursors capture: " + ex.Message);
                return null;
            }
        }

        /// <summary>Ends a background tool's capture; if the main thread is not free, later (Dispose runs once).</summary>
        static void EndCapture(AgentCursors.Capture watch)
        {
            try { Entry.Main.Run(() => { watch.Dispose(); return 0; }, 5000); }
            catch (Exception ex)
            {
                Log.Error("agent cursors capture: " + ex.Message);
                Entry.Main?.Post(watch.Dispose);
            }
        }

        /// <summary>Watch the active drawing for what a writing call creates, changes and erases (Agent Cursors). Main thread.</summary>
        static AgentCursors.Capture StartCapture(AgentStep step)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            return doc == null ? null : new AgentCursors.Capture(step, doc.Database);
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
                        if (def.Kind == ToolKind.Write && !ctx.NeedsRegen) ctx.NeedsRegen = Acad.AnyInBlockDefinition(doc.Database, tr, args);
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
