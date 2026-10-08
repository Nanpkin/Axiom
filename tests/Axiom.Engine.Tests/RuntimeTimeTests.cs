using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class RuntimeTimeTests
{
    [Fact]
    public void FractionalMillisecondDeadlineNeverRearmsWithZeroDelay()
    {
        var time = new ManualTimeProvider(); using var scheduler = new DeadlineScheduler(new(time));
        var calls = 0;
        scheduler.Schedule(TimeSpan.FromTicks(1), () => calls++);
        Assert.Equal(TimeSpan.FromMilliseconds(1), time.Timers[0].LastDue);
        time.Timers[0].FireEvenIfDisposed();
        Assert.Equal(0, calls);
        Assert.Equal(TimeSpan.FromMilliseconds(1), time.Timers[0].LastDue);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, calls); Assert.Equal(0, scheduler.ActiveTimers);
    }

    [Fact]
    public void EngineMultiplexesLogicalDeadlinesOntoOneProductionLease()
    {
        var time = new ManualTimeProvider(); var clock = new RuntimeClock(time);
        using var scheduler = new DeadlineScheduler(clock);
        using var engine = new EngineHost(clock, scheduler, new FakeExecutor());
        engine.Start(); engine.ConfigureAutomation(new FakeAuthorization(), new SequentialIds());
        engine.RegisterCapability(AutomationFixture.Binding("input"));
        var keys = Enumerable.Range(0, 30).Select(i => new Axiom.Domain.ResourceKey("input", "r" + i, Axiom.Domain.ResourceScope.Machine)).ToArray();
        foreach (var key in keys) engine.TakeOverride(key, "owner", "test", TimeSpan.FromSeconds(1));
        Assert.Equal(30, engine.PendingDeadlines); Assert.Equal(1, scheduler.ActiveTimers);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, engine.PendingDeadlines); Assert.Equal(0, scheduler.ActiveTimers);
    }

    [Fact]
    public void CancellationLateCallbackUtcJumpsAndLongDeadlineAreSafe()
    {
        var time = new ManualTimeProvider();
        var clock = new RuntimeClock(time);
        using var scheduler = new DeadlineScheduler(clock);
        Assert.Empty(time.Timers);
        var count = 0;
        var cancelled = scheduler.Schedule(TimeSpan.FromSeconds(1), () => count++);
        cancelled.Dispose();
        time.Timers[0].FireEvenIfDisposed();
        Assert.Equal(0, count);
        using var active = scheduler.Schedule(DeadlineScheduler.MaximumSlice + TimeSpan.FromSeconds(1), () => count++);
        time.JumpUtc(TimeSpan.FromDays(100));
        time.JumpUtc(TimeSpan.FromDays(-200));
        Assert.Equal(TimeSpan.Zero, clock.MonotonicNow);
        Assert.Equal(DeadlineScheduler.MaximumSlice, time.Timers[^1].LastDue);
        time.Advance(DeadlineScheduler.MaximumSlice);
        Assert.Equal(0, count);
        Assert.Equal(TimeSpan.FromSeconds(1), time.Timers[^1].LastDue);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, count);
        Assert.Equal(0, scheduler.ActiveTimers);
    }

    [Fact]
    public async Task CallbackAndRearmFaultsAreObservableAndReleaseTimers()
    {
        var time = new ManualTimeProvider();
        using var scheduler = new DeadlineScheduler(new(time));
        scheduler.Schedule(TimeSpan.Zero, () => throw new InvalidOperationException("callback"));
        time.Advance(TimeSpan.Zero);
        Assert.Equal("callback", (await scheduler.Fault).Message);
        Assert.Equal(0, scheduler.ActiveTimers);
        using var other = new DeadlineScheduler(new(time));
        other.Schedule(TimeSpan.MaxValue, () => Assert.Fail("not due"));
        time.FailChange = true;
        time.Advance(DeadlineScheduler.MaximumSlice);
        Assert.Equal("timer fault", (await other.Fault).Message);
        Assert.Equal(0, other.ActiveTimers);
        Assert.Throws<InvalidOperationException>(() => other.Schedule(TimeSpan.Zero, () => { }));
        Assert.Equal(0, other.ActiveTimers);
    }

    [Fact]
    public void PastAndEqualDeadlinesFireOnceAndDisposeInvalidatesQueuedCallbacks()
    {
        var time = new ManualTimeProvider();
        var scheduler = new DeadlineScheduler(new(time));
        var count = 0;
        scheduler.Schedule(TimeSpan.MinValue, () => count++);
        scheduler.Schedule(TimeSpan.Zero, () => count++);
        time.Advance(TimeSpan.Zero);
        Assert.Equal(2, count);
        scheduler.Schedule(TimeSpan.FromDays(1), () => count++);
        scheduler.Dispose();
        foreach (var timer in time.Timers) timer.FireEvenIfDisposed();
        Assert.Equal(2, count);
        Assert.Throws<ObjectDisposedException>(() => scheduler.Schedule(TimeSpan.Zero, () => { }));
    }

    [Fact]
    public async Task ProductionTimerSmokeCompletesBySignal()
    {
        var clock = new RuntimeClock(TimeProvider.System);
        using var scheduler = new DeadlineScheduler(clock);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Schedule(clock.MonotonicNow + TimeSpan.FromMilliseconds(10), () => done.TrySetResult());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, scheduler.ActiveTimers);
    }
}
