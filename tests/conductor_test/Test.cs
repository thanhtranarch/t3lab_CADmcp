// Conductor (the fair queue) without the host: free slot, holder state, deadline -> BusyException, order
// (never-served agent first, round robin, lane auto last), double release, one holder under 40 parallel calls.
// dotnet run --project tests/conductor_test   (exit code = number of failures)
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Q = T3LabCAD.Core.Conductor;

static class Test
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) _fail++; }

    static int Main()
    {
        var q = new Q();
        // 1 free slot: immediate, status busy while held
        using (var t = q.Enter("claude", "get_revit_context", null, 1000))
        {
            var s = q.Status();
            Check((bool)s["busy"] && (string)s["agent"] == "claude" && (string)s["state"] == "waiting_for_host", "holder shown, not started yet");
            t.MarkRunning();
            Check((string)q.Status()["state"] == "running", "MarkRunning -> running");
        }
        Check(!(bool)q.Status()["busy"], "released");

        // 2 deadline: nothing runs late
        var hold = q.Enter("claude", "create_walls", null, 0);
        hold.MarkRunning();
        int told = -1; string behind = null;
        try { q.Enter("codex", "set_parameter", null, 300, (a, r) => { told = a; behind = r; }); Check(false, "busy expected"); }
        catch (Q.BusyException be) { Check(be.Agent == "claude" && be.Tool == "create_walls" && be.Started && be.Ahead == 0, "busy names the holder: " + be.Message); }
        Check(told == 0 && behind == "claude", "onQueued(ahead=0, running=claude)");
        Check((int)q.Status()["waiting"] == 0, "timed-out waiter removed");

        // 3 round robin + lanes: claude has 3 calls queued, codex 1, a bot (auto) 1, queued in that order
        var order = new List<string>();
        var tasks = new List<Task>();
        void Queue(string agent, string tool, string lane)
        {
            var started = new ManualResetEventSlim();
            tasks.Add(Task.Run(() => { using (q.Enter(agent, tool, lane, 0, (a, r) => started.Set())) { lock (order) order.Add(tool); Thread.Sleep(20); } }));
            started.Wait(2000);
        }
        Queue("bot", "b1", "auto");
        Queue("claude", "c1", null);
        Queue("claude", "c2", null);
        Queue("claude", "c3", null);
        Queue("codex", "x1", "person");
        Check((int)q.Status()["waiting"] == 5, "5 waiting");
        hold.Dispose();
        Task.WaitAll(tasks.ToArray(), 5000);
        string got = string.Join(",", order);
        // codex never served -> before claude's c1 (claude was served by the create_walls hold); then round robin; bot (auto) last
        Check(got == "x1,c1,c2,c3,b1", "order " + got);

        // 4 dispose twice is harmless
        var t5 = q.Enter("a", "t", null, 0); t5.Dispose(); t5.Dispose();
        Check(!(bool)q.Status()["busy"], "double dispose ok");

        // 5 parallel stress: one holder at a time
        int inside = 0, maxInside = 0;
        var all = new List<Task>();
        for (int i = 0; i < 40; i++)
        {
            string ag = "agent" + (i % 4);
            all.Add(Task.Run(() => { using (q.Enter(ag, "t", null, 10000)) { int n = Interlocked.Increment(ref inside); lock (all) maxInside = Math.Max(maxInside, n); Thread.Sleep(2); Interlocked.Decrement(ref inside); } }));
        }
        Task.WaitAll(all.ToArray(), 20000);
        Check(maxInside == 1, "never two holders (max " + maxInside + ")");
        Console.WriteLine(_fail == 0 ? "ALL OK" : _fail + " FAILED");
        return _fail;
    }
}
