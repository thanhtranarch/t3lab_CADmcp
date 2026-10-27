using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    internal static class CommandTools
    {
        /// <summary>
        /// run_command drives drawing commands only: anything that loads or runs code, or starts
        /// another program, is refused (the user can still run those themselves).
        /// </summary>
        static readonly HashSet<string> BlockedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SHELL", "SH", "START", "STARTAPP", "EXEC", "NETLOAD", "APPLOAD", "ARX", "XLOAD", "LOAD",
            "VBALOAD", "VBARUN", "VBAIDE", "VBAMAN", "VBASTMT", "MACROS", "SCRIPT", "SCR", "RSCRIPT",
            "ACTPLAY", "BROWSER", "HYPERLINK", "SECURELOAD", "TRUSTEDPATHS", "LEGACYCODESEARCH", "DOTNETLOAD",
        };

        public static void Register()
        {
            Registry.Add("run_command", "Run AutoCAD command", ToolKind.App,
                "Send a command line to AutoCAD as if typed, e.g. _.ZOOM _E, -LAYER _M WALL  (spaces = Enter), _.PURGE _A * _N, " +
                "_.OVERKILL. Use command-line versions (-LAYER, -INSERT, -HATCH) so no dialog opens. Waits until the command finishes " +
                "(or wait_seconds) and reports entities created meanwhile. Commands that repeat prompts (LINE, PLINE) need an extra " +
                "Enter (two spaces) to end. A command still waiting for input when the wait ends is cancelled (Esc) unless " +
                "cancel_unfinished=false, so AutoCAD is not left busy. " +
                "AutoLISP expressions and commands that load code, run scripts or start programs are refused.",
                ObjR("command",
                    P("command", Str("Command line text. Prefix with _. for English command names")),
                    P("wait_seconds", Num("Max seconds to wait for the command to finish (default 15, max 120)")),
                    P("cancel_unfinished", Bool("Press Esc if the command is still waiting for input at the end (default true)")),
                    P("echo", Bool("Echo on the AutoCAD command line (default true)"))),
                RunCommand, background: true);

            Registry.Add("cancel_command", "Cancel running command", ToolKind.App,
                "Press Esc in the active drawing to cancel the running command. Use it for a command run_command left open, " +
                "or when the user agrees to cancel what they are doing: other write tools refuse to run while a command is active.",
                Obj(), CancelCommand, tx: false, duringCommand: true);

            Registry.Add("reload_addin", "Reload add-in", ToolKind.App,
                "Reload T3LabCAD.Core.dll from the bundle folder after installing a new build (scripts/install.ps1). " +
                "The connection drops for a moment; call get_status after a few seconds.",
                Obj(), Reload, needsDocument: false, duringCommand: true);
        }

        // ---------------------------------------------------------------- run_command

        static void Guard(string command)
        {
            if (command.IndexOf('(') >= 0 || command.IndexOf('!') >= 0)
                throw new ToolError("AutoLISP is not allowed through run_command (no '(' or '!'). Use the dedicated tools or plain command names.");
            foreach (string token in command.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = token.TrimStart('_', '.', '-', '+', '\'').TrimEnd(';');
                if (BlockedCommands.Contains(name))
                    throw new ToolError($"'{name}' loads/runs code or starts a program and is not allowed through run_command. Ask the user to run it.");
            }
        }

        sealed class CommandWatch
        {
            public Document Doc;
            public long Seed;
            public ObjectId SpaceId;
            public volatile bool Started, Cancelled, Failed;
            public readonly List<string> Commands = new List<string>();
            CommandEventHandler _start, _cancel, _fail;

            public void Hook()
            {
                _start = (s, e) => { Started = true; lock (Commands) Commands.Add(e.GlobalCommandName); };
                _cancel = (s, e) => Cancelled = true;
                _fail = (s, e) => Failed = true;
                Doc.CommandWillStart += _start;
                Doc.CommandCancelled += _cancel;
                Doc.CommandFailed += _fail;
            }

            public void Unhook()
            {
                try
                {
                    Doc.CommandWillStart -= _start;
                    Doc.CommandCancelled -= _cancel;
                    Doc.CommandFailed -= _fail;
                }
                catch (Exception)
                {
                    // the document was closed by the command
                }
            }
        }

        /// <summary>Runs on the socket thread: queue the command, then poll until AutoCAD is back at the Command: prompt.</summary>
        static object RunCommand(ToolContext unused, Args a)
        {
            string command = a.ReqStr("command");
            Guard(command);
            double waitSeconds = Math.Max(1, Math.Min(120, a.Num("wait_seconds", 15)));
            bool echo = a.Bool("echo", true);
            bool cancelUnfinished = a.Bool("cancel_unfinished", true);
            var watch = new CommandWatch();

            Entry.Main.Run(() =>
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new ToolError("No drawing is open in AutoCAD.");
                string cip = doc.CommandInProgress;
                if (!string.IsNullOrEmpty(cip))
                    throw new ToolError($"AutoCAD is busy with command '{cip}'. Ask the user to finish it or press Esc, then retry.");
                watch.Doc = doc;
                watch.Seed = doc.Database.Handseed.Value;
                watch.SpaceId = doc.Database.CurrentSpaceId;
                watch.Hook();
                string text = command.EndsWith(" ") || command.EndsWith("\n") ? command : command + "\n";
                doc.SendStringToExecute(text, true, false, echo);
                return 0;
            });

            var sw = Stopwatch.StartNew();
            string running = "";
            while (true)
            {
                Thread.Sleep(150);
                running = Entry.Main.Run(() =>
                {
                    try { return watch.Doc.CommandInProgress ?? ""; }
                    catch (Exception) { return ""; } // document closed
                });
                if (running.Length > 0) watch.Started = true;
                if (watch.Started && running.Length == 0) break;
                if (!watch.Started && sw.ElapsedMilliseconds > 2500) break;
                if (sw.Elapsed.TotalSeconds > waitSeconds) break;
            }

            string cancelled = null;
            if (running.Length > 0 && cancelUnfinished)
            {
                cancelled = running;
                running = Escape(watch.Doc);
            }

            return Entry.Main.Run(() =>
            {
                watch.Unhook();
                var result = new Dictionary<string, object>
                {
                    ["command"] = command,
                    ["started"] = watch.Started,
                    ["finished"] = running.Length == 0,
                    ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
                };
                lock (watch.Commands) result["commands_run"] = watch.Commands.Distinct().ToList();
                if (cancelled != null) result["cancelled_unfinished"] = cancelled + " was still waiting for input and was cancelled (Esc). Entities it already created stay.";
                if (running.Length > 0) result["still_running"] = running + " (waiting for input: finish it, or call cancel_command)";
                if (watch.Cancelled) result["cancelled"] = true;
                if (watch.Failed) result["failed"] = true;
                if (!watch.Started) result["note"] = "No command started: check the command name (use _. prefix and English names).";
                try { result["created"] = NewEntities(watch); }
                catch (Exception) { /* drawing closed by the command */ }
                return result;
            });
        }

        static object NewEntities(CommandWatch watch)
        {
            Database db = watch.Doc.Database;
            var described = new List<object>();
            int count = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId spaceId in new[] { watch.SpaceId, db.CurrentSpaceId }.Distinct())
                {
                    foreach (ObjectId id in (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead))
                    {
                        if (id.Handle.Value < watch.Seed || id.IsErased) continue;
                        count++;
                        if (described.Count < 50 && tr.GetObject(id, OpenMode.ForRead) is Entity e) described.Add(Describe.Entity(e, tr, false));
                    }
                }
                tr.Commit();
            }
            return new Dictionary<string, object> { ["count"] = count, ["entities"] = described };
        }

        /// <summary>Socket thread: send Esc Esc and wait up to 3 s for the Command: prompt. Returns what is still running ("" = idle).</summary>
        static string Escape(Document doc)
        {
            Entry.Main.Run(() =>
            {
                doc.SendStringToExecute("\x03\x03", true, false, false);
                return 0;
            });
            string running = "";
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(150);
                running = Entry.Main.Run(() =>
                {
                    try { return doc.CommandInProgress ?? ""; }
                    catch (Exception) { return ""; }
                });
                if (running.Length == 0) break;
            }
            return running;
        }

        static object CancelCommand(ToolContext c, Args a)
        {
            string before = c.Doc.CommandInProgress ?? "";
            if (before.Length == 0) return new Dictionary<string, object> { ["cancelled"] = null, ["note"] = "No command was running." };
            // queued now, processed once this handler returns; check with get_status (command_in_progress)
            c.Doc.SendStringToExecute("\x03\x03", true, false, false);
            return new Dictionary<string, object>
            {
                ["cancelled"] = before,
                ["note"] = "Esc sent. get_status shows command_in_progress empty once AutoCAD is back at the Command: prompt.",
            };
        }

        // ---------------------------------------------------------------- reload

        static object Reload(ToolContext c, Args a)
        {
            if (!Entry.RequestReload()) throw new ToolError("Reload is not available (the loader did not pass a reload callback).");
            return new Dictionary<string, object>
            {
                ["reloading"] = true,
                ["from"] = Entry.ContentsDir,
                ["note"] = "The add-in reloads as soon as AutoCAD is idle. Call get_status in a few seconds.",
            };
        }
    }
}
