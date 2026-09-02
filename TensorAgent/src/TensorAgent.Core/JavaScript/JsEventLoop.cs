// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace TensorAgent.Core.JavaScript;

/// <summary>
/// The event loop JavaScriptCore does not have.
///
/// <para>
/// JSC is an ECMAScript engine and nothing more: it drains its own microtask
/// queue when a call into JavaScript returns to the host, and that is the whole
/// of its scheduling. <c>setTimeout</c>, <c>setInterval</c> and
/// <c>queueMicrotask</c> are not language features, they are things a host
/// provides, so this class is what makes the four lines a model actually writes —
/// a timeout, an interval, an awaited fetch — behave the way they do in Node.
/// </para>
///
/// <para>
/// The loop runs on the one JavaScript thread and is the only thing allowed to
/// call into the VM after the main script returns. Work finished on other threads
/// (an HTTP response) comes back through <see cref="Post"/>, which merely queues a
/// closure and wakes the loop; the closure runs here, on the JS thread, where
/// touching a <c>JSValueRef</c> is legal.
/// </para>
///
/// <para>
/// Every phase is bounded by the run's deadline. A promise that never settles, an
/// interval that never stops, a timer scheduled an hour out — each ends the loop
/// with <see cref="TimedOut"/> rather than hanging the tool call, because a shell
/// that does not come back is worse for a model than one that reports 124.
/// </para>
/// </summary>
internal sealed class JsEventLoop
{
    private sealed class TimerEntry
    {
        public long Id;
        public long Sequence;
        public double DueMs;
        public double IntervalMs;
        public bool Repeating;
        public IntPtr Callback;
        public IntPtr[] Arguments = Array.Empty<IntPtr>();
    }

    private readonly JsContext _context;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<TimerEntry> _timers = new();
    private readonly Queue<IntPtr> _microtasks = new();
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private long _nextTimerId = 1;
    private long _sequence;
    private int _pendingJobs;

    internal JsEventLoop(JsContext context, TimeSpan budget)
    {
        _context = context;
        BudgetMs = budget.TotalMilliseconds;
    }

    /// <summary>Milliseconds this run may spend in total, from the moment the loop was created.</summary>
    internal double BudgetMs { get; }

    internal double ElapsedMs => _clock.Elapsed.TotalMilliseconds;

    internal double RemainingMs => BudgetMs - ElapsedMs;

    /// <summary>Set by <c>process.exit</c>; stops the loop at the next opportunity.</summary>
    internal bool ExitRequested { get; private set; }

    internal int ExitCode { get; private set; }

    /// <summary>True when the loop stopped because the deadline passed rather than because it ran dry.</summary>
    internal bool TimedOut { get; private set; }

    /// <summary>An uncaught error from a callback, which ends the run the way Node ends it.</summary>
    internal JsErrorInfo? Failure { get; private set; }

    internal void RequestExit(int exitCode)
    {
        ExitRequested = true;
        ExitCode = exitCode;
        _wake.Set();
    }

    // ---- what scripts schedule ---------------------------------------------------------

    internal long AddTimer(IntPtr callback, IntPtr[] arguments, double delayMs, bool repeating)
    {
        // Node clamps a negative or non-finite delay to 1 ms and treats 0 the same way.
        if (double.IsNaN(delayMs) || delayMs < 1)
            delayMs = 1;
        _context.Protect(callback);
        foreach (IntPtr argument in arguments)
            _context.Protect(argument);
        var entry = new TimerEntry
        {
            Id = _nextTimerId++,
            Sequence = _sequence++,
            DueMs = ElapsedMs + delayMs,
            IntervalMs = delayMs,
            Repeating = repeating,
            Callback = callback,
            Arguments = arguments,
        };
        _timers.Add(entry);
        _wake.Set();
        return entry.Id;
    }

    internal void ClearTimer(long id)
    {
        for (int i = 0; i < _timers.Count; i++)
        {
            if (_timers[i].Id != id)
                continue;
            Release(_timers[i]);
            _timers.RemoveAt(i);
            return;
        }
    }

    internal void QueueMicrotask(IntPtr callback)
    {
        _context.Protect(callback);
        _microtasks.Enqueue(callback);
        _wake.Set();
    }

    // ---- what host work uses -----------------------------------------------------------

    /// <summary>Announces work in flight off the JS thread, so the loop waits for it.</summary>
    internal void StartJob() => Interlocked.Increment(ref _pendingJobs);

    /// <summary>
    /// Hands a closure back to the JS thread. Safe from any thread; the loop runs
    /// it and then decrements the in-flight count.
    /// </summary>
    internal void Post(Action work)
    {
        _posted.Enqueue(work);
        _wake.Set();
    }

    private bool HasWork => _timers.Count > 0 || _microtasks.Count > 0 || !_posted.IsEmpty || Volatile.Read(ref _pendingJobs) > 0;

    // ---- the loop ----------------------------------------------------------------------

    /// <summary>
    /// Runs until there is nothing left to do, the script exits, a callback throws,
    /// or the deadline passes.
    /// </summary>
    internal void Run()
    {
        while (!ExitRequested && Failure is null)
        {
            if (RemainingMs <= 0)
            {
                TimedOut = HasWork;
                return;
            }

            if (!RunPosted() || !DrainMicrotasks())
                return;

            if (!HasWork)
                return;

            TimerEntry? due = NextDueTimer(out double waitMs);
            if (due is not null && waitMs <= 0)
            {
                FireTimer(due);
                continue;
            }

            // Nothing is runnable yet: either a timer is still in the future or a
            // host job is in flight. Sleep until whichever comes first, but never
            // past the deadline — this is where a promise that never settles ends.
            double sleep = Math.Min(waitMs, RemainingMs);
            if (sleep <= 0)
            {
                TimedOut = true;
                return;
            }
            _wake.Reset();
            if (!_posted.IsEmpty)
                continue;
            _wake.Wait(TimeSpan.FromMilliseconds(Math.Min(sleep, 50)));
        }
    }

    /// <summary>The earliest timer, with how long until it is due (positive means "not yet").</summary>
    private TimerEntry? NextDueTimer(out double waitMs)
    {
        TimerEntry? best = null;
        foreach (TimerEntry timer in _timers)
        {
            if (best is null || timer.DueMs < best.DueMs || (timer.DueMs == best.DueMs && timer.Sequence < best.Sequence))
                best = timer;
        }
        waitMs = best is null ? double.MaxValue : best.DueMs - ElapsedMs;
        return best;
    }

    private void FireTimer(TimerEntry timer)
    {
        if (timer.Repeating)
        {
            timer.DueMs = ElapsedMs + timer.IntervalMs;
            timer.Sequence = _sequence++;
        }
        else
        {
            _timers.Remove(timer);
        }

        _context.ArmWatchdog(RemainingMs / 1000.0);
        _context.TryCall(timer.Callback, _context.Global, timer.Arguments, out JsErrorInfo? error);
        if (!timer.Repeating)
            Release(timer);
        Observe(error);
    }

    private bool DrainMicrotasks()
    {
        while (_microtasks.Count > 0)
        {
            if (RemainingMs <= 0)
            {
                TimedOut = true;
                return false;
            }
            IntPtr callback = _microtasks.Dequeue();
            _context.ArmWatchdog(RemainingMs / 1000.0);
            _context.TryCall(callback, _context.Global, Array.Empty<IntPtr>(), out JsErrorInfo? error);
            _context.Unprotect(callback);
            Observe(error);
            if (ExitRequested || Failure is not null)
                return false;
        }
        return true;
    }

    private bool RunPosted()
    {
        while (_posted.TryDequeue(out Action? work))
        {
            try
            {
                _context.ArmWatchdog(Math.Max(RemainingMs, 0) / 1000.0);
                work();
            }
            catch (JsScriptException ex)
            {
                Failure = ex.Info;
                return false;
            }
            finally
            {
                Interlocked.Decrement(ref _pendingJobs);
            }
            if (ExitRequested)
                return false;
        }
        return true;
    }

    /// <summary>Records an uncaught callback error, unless it is only <c>process.exit</c> unwinding.</summary>
    private void Observe(JsErrorInfo? error)
    {
        if (error is null)
            return;
        if (error.ExitCode is int code)
        {
            RequestExit(code);
            return;
        }
        if (error.Terminated)
        {
            TimedOut = true;
            Failure = error;
            return;
        }
        Failure = error;
    }

    private void Release(TimerEntry timer)
    {
        _context.Unprotect(timer.Callback);
        foreach (IntPtr argument in timer.Arguments)
            _context.Unprotect(argument);
    }

    /// <summary>Drops every pending timer's protection; the context is about to go.</summary>
    internal void Shutdown()
    {
        foreach (TimerEntry timer in _timers)
            Release(timer);
        _timers.Clear();
        while (_microtasks.Count > 0)
            _context.Unprotect(_microtasks.Dequeue());
        _wake.Dispose();
    }
}
