using System.Reflection;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class SchedulerConcurrencyReviewTests
{
    private static readonly TimeSpan FailureLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task CancellingWhileProductionCallbackWaitsAtEngineGatePreservesConfirmedMode()
    {
        using var f = new Scenario();
        f.Engine.RequestMode(EngineMode.Lightweight, Interval);
        var oldBoundary = f.Boundaries.Last;
        var callbacks = await Task.Run(() =>
        {
            lock (f.EngineGate)
            {
                oldBoundary.Proceed.Set();
                var firing = Task.Run(() => f.Time.Advance(Interval));
                Wait(oldBoundary.WaitingAtEngineGate);
                // This getter needs the scheduler gate. Reaching it proves the callback
                // has left that gate while cancellation still owns the Engine gate.
                Assert.Equal(0, f.Scheduler.ActiveTimers);
                var plan = f.Engine.GetObservationPlan();
                Assert.True(f.Engine.AcknowledgeObservationPlan(plan.Version, []));
                Assert.Equal(TransitionStatus.Effective, f.Engine.CurrentMode.Status);
                Assert.Equal(0, f.Engine.PendingDeadlines);
                return new[] { firing };
            }
        }).WaitAsync(FailureLimit);

        await callbacks[0].WaitAsync(FailureLimit);
        Assert.Equal(EngineMode.Lightweight, f.Engine.CurrentMode.Effective);
        Assert.Equal(TransitionStatus.Effective, f.Engine.CurrentMode.Status);
        Assert.Equal(0, f.Engine.PendingDeadlines);
        Assert.Equal(0, f.Scheduler.ActiveTimers);
        Assert.False(f.Scheduler.Fault.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldProductionCallbackCannotConsumeDueReplacementOrNewLifecycle(bool restart)
    {
        using var f = new Scenario();
        var originalMode = f.Engine.RequestMode(EngineMode.Lightweight, Interval);
        var oldBoundary = f.Boundaries.Last;
        var pending = await Task.Run(() =>
        {
            lock (f.EngineGate)
            {
                oldBoundary.Proceed.Set();
                var oldCallback = Task.Run(() => f.Time.Advance(Interval));
                Wait(oldBoundary.WaitingAtEngineGate);
                Assert.Equal(0, f.Scheduler.ActiveTimers);

                if (restart)
                {
                    f.Engine.Stop();
                    Assert.Equal(0, f.Engine.PendingDeadlines);
                    f.Engine.Start();
                    f.Engine.ConfigureAutomation(new FakeAuthorization(), new SequentialIds());
                }
                var currentMode = f.Engine.RequestMode(EngineMode.Normal, Interval);
                Assert.True(currentMode.Generation > originalMode.Generation);
                var currentBoundary = f.Boundaries.Last;
                Assert.NotSame(oldBoundary, currentBoundary);
                // Make the replacement genuinely due, but hold its own callback at an
                // explicit barrier. Only the old callback can enter Engine next.
                var currentCallback = Task.Run(() => f.Time.Advance(Interval));
                Wait(currentBoundary.Entered);
                Assert.False(currentBoundary.Proceed.IsSet);
                Assert.Equal(0, f.Scheduler.ActiveTimers);
                return (oldCallback, currentCallback, currentBoundary, currentMode);
            }
        }).WaitAsync(FailureLimit);

        try
        {
            await pending.oldCallback.WaitAsync(FailureLimit);
            Assert.True(f.Engine.IsRunning);
            Assert.Equal(pending.currentMode.Generation, f.Engine.CurrentMode.Generation);
            Assert.Equal(TransitionStatus.Pending, f.Engine.CurrentMode.Status);
            Assert.Equal(1, f.Engine.PendingDeadlines);
            Assert.Null(f.Engine.RuntimeFault);
            Assert.False(f.Scheduler.Fault.IsCompleted);
        }
        finally { pending.currentBoundary.Proceed.Set(); }

        // Positive control: the replacement's own callback still expires its mode.
        await pending.currentCallback.WaitAsync(FailureLimit);
        Assert.Equal(pending.currentMode.Generation, f.Engine.CurrentMode.Generation);
        Assert.Equal(TransitionStatus.Failed, f.Engine.CurrentMode.Status);
        Assert.Contains("timed out", f.Engine.CurrentMode.Reason, StringComparison.Ordinal);
        Assert.Equal(0, f.Engine.PendingDeadlines);
        Assert.Equal(0, f.Scheduler.ActiveTimers);
        Assert.False(f.Scheduler.Fault.IsCompleted);
    }

    private static void Wait(ManualResetEventSlim signal) =>
        Assert.True(signal.Wait(FailureLimit), "Expected callback barrier was not reached within 10 seconds.");

    private sealed class Scenario : IDisposable
    {
        internal readonly ManualTimeProvider Time = new();
        internal readonly DeadlineScheduler Scheduler;
        internal readonly CallbackBoundaries Boundaries;
        internal readonly EngineHost Engine;
        internal readonly object EngineGate;

        internal Scenario()
        {
            var clock = new RuntimeClock(Time);
            Scheduler = new(clock);
            Boundaries = new(Scheduler);
            Engine = new(clock, Boundaries, new FakeExecutor());
            EngineGate = typeof(EngineHost).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Engine)!;
            Boundaries.EngineGate = EngineGate;
            Engine.Start();
            Engine.ConfigureAutomation(new FakeAuthorization(), new SequentialIds());
        }

        public void Dispose()
        {
            Boundaries.ReleaseAll();
            Engine.Dispose();
            Scheduler.Dispose();
        }
    }

    // This decorator controls callback entry only. Deadlines, timer disposal, and
    // scheduler locking all execute in the production DeadlineScheduler.
    private sealed class CallbackBoundaries(DeadlineScheduler scheduler) : IDeadlineScheduler
    {
        private readonly List<CallbackBoundary> callbacks = [];
        internal object EngineGate = null!;
        internal CallbackBoundary Last => callbacks[^1];

        public IDisposable Schedule(TimeSpan deadline, Action callback)
        {
            var boundary = new CallbackBoundary();
            callbacks.Add(boundary);
            return scheduler.Schedule(deadline, () => boundary.Invoke(EngineGate, callback));
        }

        internal void ReleaseAll()
        {
            foreach (var boundary in callbacks) boundary.Proceed.Set();
        }
    }

    private sealed class CallbackBoundary
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Proceed = new();
        internal readonly ManualResetEventSlim WaitingAtEngineGate = new();

        internal void Invoke(object engineGate, Action callback)
        {
            Entered.Set();
            Wait(Proceed);
            if (!Monitor.TryEnter(engineGate))
            {
                // A failed acquisition proves that this dispatched callback cannot
                // enter Engine. The extra monitor is reentrant when callback runs.
                WaitingAtEngineGate.Set();
                if (!Monitor.TryEnter(engineGate, FailureLimit))
                    throw new TimeoutException("Callback could not acquire Engine gate within 10 seconds.");
            }
            try { callback(); }
            finally { Monitor.Exit(engineGate); }
        }
    }
}
