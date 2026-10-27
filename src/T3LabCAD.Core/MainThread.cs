using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace T3LabCAD.Core
{
    /// <summary>
    /// Runs work on the AutoCAD main thread (application context) from socket threads.
    /// A hidden control created on the main thread receives BeginInvoke messages, which
    /// AutoCAD's message loop dispatches between user actions.
    /// </summary>
    internal sealed class MainThread : IDisposable
    {
        const int Pending = 0, Running = 1, Cancelled = 2;
        const int MaxRunMs = 10 * 60 * 1000;

        readonly Control _control;

        public MainThread()
        {
            _control = new Control();
            _control.CreateControl();
            IntPtr unused = _control.Handle; // force the window handle on this (main) thread
        }

        /// <summary>
        /// Runs fn on the main thread. If it has not started within startTimeoutMs it is
        /// cancelled (it will never run), so a timed-out write never happens later.
        /// applicationContext: messages handled while AutoCAD waits at the Command: prompt run in
        /// that drawing's context, where switching to another drawing (new/open/activate/close)
        /// suspends the caller until the first drawing is active again. Such work goes through
        /// ExecuteInApplicationContext instead.
        /// </summary>
        public T Run<T>(Func<T> fn, int startTimeoutMs = 60000, bool applicationContext = false)
        {
            var item = new WorkItem<T>(fn);
            MethodInvoker start = applicationContext ? () => StartInApplicationContext(item) : (MethodInvoker)item.Execute;
            try
            {
                _control.BeginInvoke(start);
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                throw new ToolError("The T3LabCAD server is stopping. Retry in a moment.");
            }

            if (!item.Done.Wait(startTimeoutMs))
            {
                if (Interlocked.CompareExchange(ref item.State, Cancelled, Pending) == Pending)
                    throw new ToolError($"AutoCAD did not respond within {startTimeoutMs / 1000}s (a modal dialog or a long operation is blocking it). Nothing was executed.");
                item.Done.Wait(MaxRunMs); // it started: wait for it to finish
            }
            if (item.Error != null) ExceptionDispatchInfo.Capture(item.Error).Throw();
            return item.Result;
        }

        static void StartInApplicationContext<T>(WorkItem<T> item)
        {
            try
            {
                DocumentCollection dm = AcApp.DocumentManager;
                if (dm.IsApplicationContext) item.Execute();
                else dm.ExecuteInApplicationContext(_ => item.Execute(), null);
            }
            catch (Exception ex)
            {
                item.Fail(ex);
            }
        }

        public void Dispose()
        {
            try { _control.Dispose(); } catch { }
        }

        sealed class WorkItem<T>
        {
            readonly Func<T> _fn;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public int State = Pending;
            public T Result;
            public Exception Error;

            public WorkItem(Func<T> fn)
            {
                _fn = fn;
            }

            public void Execute()
            {
                if (Interlocked.CompareExchange(ref State, Running, Pending) != Pending) return;
                try { Result = _fn(); }
                catch (Exception ex) { Error = ex; }
                finally { Done.Set(); }
            }

            /// <summary>Could not even be scheduled: report the error unless it already ran.</summary>
            public void Fail(Exception ex)
            {
                if (Interlocked.CompareExchange(ref State, Running, Pending) != Pending) return;
                Error = ex;
                Done.Set();
            }
        }
    }
}
