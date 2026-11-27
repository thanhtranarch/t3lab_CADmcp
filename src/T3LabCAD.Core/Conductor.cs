using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace T3LabCAD.Core
{
    /// <summary>
    /// The one AutoCAD slot, shared fairly by every AI app (after T3Lab pyRevit core/conductor.py). Order: lane class
    /// (person / lead 1, task 2, auto 3; one class up per AgingMs waited), then the agent served longest ago (round robin),
    /// then arrival. A call that cannot start before its deadline gets BusyException and never runs later, so a client
    /// that gave up never sees a "ghost write". Pure .NET: no AutoCAD API here (tests compile this file alone).
    /// </summary>
    internal sealed class Conductor
    {
        public const int AgingMs = 15000;

        internal sealed class Slot
        {
            public string Agent, Tool;
            public DateTime Since;
            public bool Running;        // false = holds the slot, AutoCAD has not started it yet (dialog, edit mode)
        }

        sealed class Waiter
        {
            public long Seq;
            public string Agent, Tool;
            public int Class;
            public DateTime Arrived;
        }

        internal sealed class BusyException : Exception
        {
            public readonly string Agent, Tool;
            public readonly double RunningS;
            public readonly bool Started;
            public readonly int Ahead;

            public BusyException(Slot holder, int ahead, double waitedS)
                : base(holder == null
                    ? $"The call did not get its turn within {waitedS:0} s."
                    : $"Busy with {holder.Tool} for {holder.Agent} ({(DateTime.UtcNow - holder.Since).TotalSeconds:0} s); the call waited {waitedS:0} s.")
            {
                Agent = holder?.Agent;
                Tool = holder?.Tool;
                RunningS = holder == null ? 0 : Math.Round((DateTime.UtcNow - holder.Since).TotalSeconds, 1);
                Started = holder?.Running ?? false;
                Ahead = ahead;
            }
        }

        internal sealed class Ticket : IDisposable
        {
            readonly Conductor _owner;
            readonly Slot _slot;
            int _released;

            public Ticket(Conductor owner, Slot slot)
            {
                _owner = owner;
                _slot = slot;
            }

            /// <summary>AutoCAD started the call (main thread).</summary>
            public void MarkRunning()
            {
                lock (_owner._gate) _slot.Running = true;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) _owner.Release(_slot);
            }
        }

        readonly object _gate = new object();
        readonly List<Waiter> _waiting = new List<Waiter>();
        readonly Dictionary<string, DateTime> _served = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        Slot _holder;
        long _seq;

        public static int ClassOf(string lane)
        {
            switch ((lane ?? "").Trim().ToLowerInvariant())
            {
                case "task": return 2;
                case "auto": return 3;
                default: return 1;   // person, lead, or a bridge that does not say
            }
        }

        /// <summary>
        /// Waits for the slot (deadlineMs &lt;= 0: no limit). onQueued(ahead, runningAgent) runs once, outside the lock,
        /// when the call has to wait. Throws BusyException when the deadline passes first; nothing ran then.
        /// </summary>
        public Ticket Enter(string agent, string tool, string lane, int deadlineMs, Action<int, string> onQueued = null)
        {
            var me = new Waiter
            {
                Agent = string.IsNullOrEmpty(agent) ? "ai" : agent,
                Tool = tool ?? "",
                Class = ClassOf(lane),
                Arrived = DateTime.UtcNow,
            };
            DateTime? until = deadlineMs > 0 ? me.Arrived.AddMilliseconds(deadlineMs) : (DateTime?)null;
            int ahead;
            string running;
            lock (_gate)
            {
                me.Seq = ++_seq;
                _waiting.Add(me);
                if (_holder == null && Next() == me) return Grant(me);
                ahead = AheadOf(me);
                running = _holder?.Agent;
            }
            try { onQueued?.Invoke(ahead, running); }
            catch (Exception) { /* display only */ }

            lock (_gate)
            {
                while (!(_holder == null && Next() == me))
                {
                    int wait = 1000;   // wake up now and then: aging changes the order
                    if (until.HasValue)
                    {
                        double left = (until.Value - DateTime.UtcNow).TotalMilliseconds;
                        if (left <= 0)
                        {
                            ahead = AheadOf(me);
                            _waiting.Remove(me);
                            Monitor.PulseAll(_gate);
                            throw new BusyException(_holder, ahead, (DateTime.UtcNow - me.Arrived).TotalSeconds);
                        }
                        wait = (int)Math.Max(1, Math.Min(wait, Math.Ceiling(left)));
                    }
                    Monitor.Wait(_gate, wait);
                }
                return Grant(me);
            }
        }

        Ticket Grant(Waiter me)
        {
            _waiting.Remove(me);
            _holder = new Slot { Agent = me.Agent, Tool = me.Tool, Since = DateTime.UtcNow };
            _served[me.Agent] = _holder.Since;
            return new Ticket(this, _holder);
        }

        void Release(Slot slot)
        {
            lock (_gate)
            {
                if (_holder == slot) _holder = null;
                Monitor.PulseAll(_gate);
            }
        }

        int Effective(Waiter w, DateTime now) => Math.Max(0, w.Class - (int)((now - w.Arrived).TotalMilliseconds / AgingMs));

        DateTime Served(string agent) => _served.TryGetValue(agent, out DateTime t) ? t : DateTime.MinValue;

        IEnumerable<Waiter> Ordered()
        {
            DateTime now = DateTime.UtcNow;
            return _waiting.OrderBy(w => Effective(w, now)).ThenBy(w => Served(w.Agent)).ThenBy(w => w.Seq);
        }

        Waiter Next() => Ordered().FirstOrDefault();

        int AheadOf(Waiter me) => Ordered().TakeWhile(w => w != me).Count();

        /// <summary>For health and the Agent Cursors bar: who holds AutoCAD and how many wait. Never blocks for long.</summary>
        public Dictionary<string, object> Status()
        {
            lock (_gate)
            {
                var d = new Dictionary<string, object>
                {
                    ["busy"] = _holder != null,
                    ["waiting"] = _waiting.Count,
                };
                if (_holder != null)
                {
                    d["agent"] = _holder.Agent;
                    d["tool"] = _holder.Tool;
                    d["state"] = _holder.Running ? "running" : "waiting_for_host";
                    d["running_s"] = Math.Round((DateTime.UtcNow - _holder.Since).TotalSeconds, 1);
                }
                if (_waiting.Count > 0) d["waiting_agents"] = Ordered().Select(w => (object)w.Agent).Distinct().ToList();
                return d;
            }
        }
    }
}
