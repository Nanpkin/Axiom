using Axiom.Domain;
using static Axiom.Engine.Tests.AutomationFixture;
using static Axiom.Engine.Tests.RuntimeFixture;

namespace Axiom.Engine.Tests;

public sealed class RuntimeRecoveryTests
{
    private static async Task NextSnapshot(StateSubscription subscription)
    {
        while ((await Within(subscription.ReadAsync().AsTask())).Kind != DeliveryKind.SnapshotApplied) { }
    }

    [Fact]
    public async Task PlanReplacedDuringAcquireReleasesObsoleteLeaseAndNeverExecutes()
    {
        var f = new RuntimeFixture(); var entered = Barrier(); var acquire = Barrier();
        f.Observer.BeforeAcquire = _ => { entered.TrySetResult(); return acquire.Task; };
        var start = f.Start(); await Within(entered.Task);
        f.Engine.RemoveRule("rule");
        acquire.SetResult();
        await Within(start);
        // Latest-plan convergence must occur while running, without Stop doing the cleanup.
        Assert.True(f.Engine.IsRunning);
        Assert.Empty(f.Host.Held);
        Assert.Empty(f.Observer.Leases); Assert.Equal(0, f.Executor.Count);
        await f.Stop();
    }

    [Fact]
    public async Task ModePauseReleaseAndResumeSnapshotDoNotReplayCommands()
    {
        var f = new RuntimeFixture(); await Within(f.Start(command: true, keep: false));
        using var changes = f.Engine.Subscribe([Input]).Subscription;
        f.Engine.RequestMode(EngineMode.Lightweight);
        await Within(f.Observer.Released.Reader.ReadAsync().AsTask());
        await Within(f.Observer.Released.Reader.ReadAsync().AsTask());
        // Only released inputs become Unknown; no inactive resource is sampled.
        Assert.Equal(StateQuality.Unknown, (await Within(changes.ReadAsync().AsTask())).State!.Observation.Quality);
        Assert.Empty(f.Host.Held);
        f.Observer.Values[Input] = true;
        f.Engine.RequestMode(EngineMode.Normal);
        await NextSnapshot(changes);
        Assert.Equal(0, f.Executor.Count);
        f.Observer.Emit(Input, false); f.Observer.Emit(Input, true);
        var call = await Within(f.Executor.NextAsync()); call.Complete();
        await Within(f.Engine.WaitForIdleAsync());
        Assert.Equal(1, f.Executor.Count);
        await f.Stop();
    }

    [Fact]
    public async Task StopDuringSnapshotAndReleaseHasOneBudgetAndPreservesResidual()
    {
        var f = new RuntimeFixture(); var snapshot = Barrier(); var release = Barrier(); var releasing = Barrier();
        f.Observer.BeforeSnapshot = () => snapshot.Task;
        f.Observer.BeforeRelease = _ => { releasing.TrySetResult(); return release.Task; };
        var start = f.Start(); await Within(f.Observer.Snapshots.Reader.ReadAsync().AsTask());
        var stop = f.Host.StopAsync(TimeSpan.FromSeconds(2));
        f.Time.Advance(TimeSpan.FromSeconds(1)); snapshot.SetResult();
        await Within(releasing.Task);
        f.Time.Advance(TimeSpan.FromSeconds(1));
        var result = await Within(stop);
        Assert.False(result.Completed); Assert.Equal(2, result.Held.Count); Assert.Equal(0, f.Executor.Count);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(start));
        await Within(f.Host.Cleanup); await f.Stop();
    }

    [Fact]
    public async Task GapDuringRecoverySnapshotRequiresAnotherExplicitRecovery()
    {
        var f = new RuntimeFixture(); await Within(f.Start(command: true));
        using var changes = f.Engine.Subscribe([Input]).Subscription;
        var snapshot = Barrier(); f.Observer.BeforeSnapshot = () => snapshot.Task;
        while (f.Observer.Snapshots.Reader.TryRead(out _)) { }
        f.Host.Recover(); await Within(f.Observer.Snapshots.Reader.ReadAsync().AsTask());
        f.Observer.Gap();
        snapshot.SetResult();
        // Explicit recovery supersedes the interrupted operation, even if its completion is still queued.
        f.Observer.BeforeSnapshot = null;
        f.Host.Recover();
        await NextSnapshot(changes);
        Assert.Equal(0, f.Executor.Count);
        f.Observer.Emit(Input, true);
        var call = await Within(f.Executor.NextAsync()); call.Complete();
        await Within(f.Engine.WaitForIdleAsync());
        await f.Stop();
    }

    [Fact]
    public async Task RetainedRulesKeepSameDecisionsAcrossModesWithoutDuplicateSubscriptions()
    {
        foreach (var lightweight in new[] { false, true })
        {
            var f = new RuntimeFixture(); await Within(f.Start(command: true));
            if (lightweight) f.Engine.RequestMode(EngineMode.Lightweight);
            f.Observer.Emit(Input, true);
            var call = await Within(f.Executor.NextAsync()); call.Complete();
            await Within(f.Engine.WaitForIdleAsync());
            Assert.Equal(TruthValue.True, f.Engine.GetRuleEvaluation("rule").Value);
            Assert.Equal(1, f.Executor.Count); Assert.Equal(2, f.Observer.History.Count);
            await f.Stop();
        }
    }

    [Fact]
    public async Task SchedulerCallbackFaultStopsHostAndCleansUp()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        f.Scheduler.Schedule(f.Clock.MonotonicNow, () => throw new InvalidOperationException("injected callback fault"));
        f.Time.Advance(TimeSpan.Zero);
        await Within(f.Host.Cleanup);
        Assert.False(f.Engine.IsRunning);
        Assert.Contains("Scheduler fault", f.Host.Error);
        Assert.Empty(f.Host.Held);
        await f.Stop();
    }

    [Fact]
    public void EngineStopsAndReportsScheduleCreationFailure()
    {
        var time = new ManualTimeProvider();
        using var scheduler = new Axiom.Infrastructure.DeadlineScheduler(new(time));
        using var engine = new EngineHost(new Axiom.Infrastructure.RuntimeClock(time), scheduler, new FakeExecutor());
        engine.Start(); engine.ConfigureAutomation(new FakeAuthorization(), new SequentialIds());
        engine.RegisterCapability(Binding("input"));
        time.FailChange = true;
        Assert.Throws<InvalidOperationException>(() => engine.TakeOverride(Input, "owner", "test", TimeSpan.FromSeconds(1)));
        Assert.False(engine.IsRunning); Assert.Contains("scheduling failed", engine.RuntimeFault);
        Assert.Equal(0, engine.PendingDeadlines); Assert.Equal(0, scheduler.ActiveTimers);
    }

    [Fact]
    public async Task TimerFailureBeforeDispatchCannotLeavePhantomFlight()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        f.Time.FailChange = true;
        f.Observer.Emit(Input, true);
        await Within(f.Host.Cleanup);
        Assert.Equal(0, f.Executor.Count);
        Assert.Equal(0, f.Engine.InFlightExecutions);
        Assert.Empty(f.Host.Held);
        Assert.NotNull(f.Engine.RuntimeFault);
    }

    [Fact]
    public async Task ReleasingUnrelatedObserverPreservesRetainedContinuousDuration()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        f.Engine.SetRule(new("rule", 2, "owner", ResourceScope.Machine,
            RuleCondition.Equal(Input, StateValue.From(Schema, true)), RuleAction.Desired(Output, StateValue.From(Schema, true)),
            timing: new(TimeSpan.Zero, TimeSpan.FromSeconds(5))));
        while (f.Observer.Acquired.Reader.TryRead(out _)) { }
        f.Engine.SetRule(new("pausable", 1, "owner", ResourceScope.Machine,
            RuleCondition.Equal(Noise, StateValue.From(Schema, true)), RuleAction.Desired(Noise, StateValue.From(Schema, true)), retention: new(false)));
        await Within(f.Observer.Acquired.Reader.ReadAsync().AsTask());
        using var input = f.Engine.Subscribe([Input]).Subscription;
        f.Observer.Emit(Input, true);
        await Within(input.ReadAsync().AsTask());
        f.Time.Advance(TimeSpan.FromSeconds(2));
        f.Engine.RequestMode(EngineMode.Lightweight);
        Assert.Equal(Noise, await Within(f.Observer.Released.Reader.ReadAsync().AsTask()));
        Assert.Equal(StateQuality.Known, f.Engine.ReadSnapshot().States[Input].Observation.Quality);
        f.Time.Advance(TimeSpan.FromSeconds(3));
        var call = await Within(f.Executor.NextAsync());
        Assert.Equal(Output, call.Desired!.Key);
        call.Complete(); await Within(f.Engine.WaitForIdleAsync());
        Assert.False(input.TryRead(out _));
        await f.Stop();
    }

    [Fact]
    public async Task WeakNewObserverNeverPublishesActionableSnapshotAndRecoveryRebindsAllLeases()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        while (f.Observer.Snapshots.Reader.TryRead(out _)) { }
        using var output = f.Engine.Subscribe([Output]).Subscription;
        f.Observer.Weak = true; f.Observer.Values[Noise] = true;
        f.Engine.SetRule(new("new", 1, "owner", ResourceScope.Machine,
            RuleCondition.Equal(Noise, StateValue.From(Schema, true)), RuleAction.Desired(Noise, StateValue.From(Schema, false))));
        Assert.Equal(StateQuality.Unknown, (await Within(output.ReadAsync().AsTask())).State!.Observation.Quality);
        Assert.False(f.Observer.Snapshots.Reader.TryRead(out _));
        Assert.Equal(0, f.Executor.Count);
        f.Observer.Weak = false;
        f.Host.Recover();
        var call = await Within(f.Executor.NextAsync());
        Assert.Equal(Noise, call.Desired!.Key);
        call.Complete(); await Within(f.Engine.WaitForIdleAsync());
        await f.Stop();
    }
}
