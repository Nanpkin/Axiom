using System.Reflection;
using Axiom.Domain;
using static Axiom.Engine.Tests.AutomationFixture;
using static Axiom.Engine.Tests.RuntimeFixture;

namespace Axiom.Engine.Tests;

public sealed class RuntimeReviewRegressionTests
{
    private static RuleDefinition NoiseRule(long version = 1, TimeSpan? age = null) => new("noise", version, "owner", ResourceScope.Machine,
        RuleCondition.Equal(Noise, StateValue.From(Schema, true)), RuleAction.Desired(Noise, StateValue.From(Schema, false)),
        timing: new(maximumInputAge: age));
    private static object HostGate(RuntimeFixture f) => typeof(Axiom.Infrastructure.ControlledRuntimeHost)
        .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Host)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R1_UnrelatedPendingObserverCannotFreezeFalse(bool snapshotPhase)
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        f.Engine.SetRule(new("rule", 2, "owner", ResourceScope.Machine,
            RuleCondition.Equal(Input, StateValue.From(Schema, true)), RuleAction.Desired(Output, StateValue.From(Schema, true)),
            timing: new(TimeSpan.Zero, TimeSpan.FromSeconds(5))));
        using var input = f.Engine.Subscribe([Input]).Subscription;
        f.Observer.Emit(Input, true); await Within(input.ReadAsync().AsTask());
        f.Time.Advance(TimeSpan.FromSeconds(2));
        var entered = Barrier(); var resume = Barrier();
        if (snapshotPhase) f.Observer.BeforeSnapshot = () => { entered.TrySetResult(); return resume.Task; };
        else f.Observer.BeforeAcquire = key => { if (key == Noise) { entered.TrySetResult(); return resume.Task; } return Task.CompletedTask; };
        f.Engine.SetRule(NoiseRule()); await Within(entered.Task);
        try
        {
            f.Observer.Emit(Input, false);
            var inactive = await Within(input.ReadAsync().AsTask());
            Assert.False(inactive.State!.Observation.Value!.Boolean);
            f.Time.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal(0, f.Engine.PendingExecutions + f.Engine.InFlightExecutions);
            Assert.Equal(0, f.Executor.Count);
        }
        finally { resume.TrySetResult(); await f.Stop(); }
    }

    [Fact]
    public async Task R2_ReleaseReservationExcludesNewDispatchUntilFreshObservation()
    {
        var f = new RuntimeFixture(); await Within(f.Start(cancellable: false));
        var releasing = Barrier(); var release = Barrier();
        f.Observer.BeforeRelease = _ => { releasing.TrySetResult(); return release.Task; };
        f.Engine.RemoveRule("rule"); await Within(releasing.Task);
        try
        {
            f.Engine.SetRule(new("rule", 2, "owner", ResourceScope.Machine,
                RuleCondition.Equal(Input, StateValue.From(Schema, false)), RuleAction.Desired(Output, StateValue.From(Schema, true))));
            Assert.Equal(0, f.Engine.PendingExecutions + f.Engine.InFlightExecutions);
            Assert.Equal(0, f.Executor.Count);
        }
        finally
        {
            var stop = f.Host.StopAsync(TimeSpan.FromSeconds(5));
            if (f.Engine.InFlightExecutions != 0) (await Within(f.Executor.NextAsync())).Complete();
            release.TrySetResult(); Assert.True((await Within(stop)).Completed);
        }
    }

    [Fact]
    public async Task R3_TightenedRequirementRejectsOldSnapshotBeforePublication()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        f.Observer.MaximumDelay = TimeSpan.FromSeconds(1); f.Observer.Values[Noise] = true;
        var captured = Barrier(); var snapshot = Barrier(); var nextPlan = Barrier(); var continuePlan = Barrier();
        f.Observer.BeforeSnapshot = () => { captured.TrySetResult(); return snapshot.Task; };
        f.Engine.SetRule(NoiseRule()); await Within(captured.Task);
        f.Host.BeforePlanIteration = () => { nextPlan.TrySetResult(); return continuePlan.Task; };
        f.Engine.SetRule(NoiseRule(2, TimeSpan.FromMilliseconds(500)));
        try
        {
            snapshot.TrySetResult(); await Within(nextPlan.Task);
            Assert.NotEqual(StateQuality.Known, f.Engine.ReadSnapshot().States[Noise].Observation.Quality);
            Assert.NotEqual(TruthValue.True, f.Engine.GetRuleEvaluation("noise").Value);
            Assert.Equal(0, f.Engine.PendingExecutions + f.Engine.InFlightExecutions);
            Assert.Equal(0, f.Executor.Count);
        }
        finally
        {
            f.Host.BeforePlanIteration = null; continuePlan.TrySetResult();
            var stop = f.Host.StopAsync(TimeSpan.FromSeconds(5));
            if (f.Engine.InFlightExecutions != 0) (await Within(f.Executor.NextAsync())).Complete();
            Assert.True((await Within(stop)).Completed);
        }
    }

    [Fact]
    public async Task R4_ConfirmationTimerFailureDoesNotPoisonWorkerCleanup()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        f.Observer.Emit(Input, true); var call = await Within(f.Executor.NextAsync());
        f.Time.FailNextChanges = 1; call.Complete();
        await Within(f.Host.Cleanup);
        Assert.Equal(0, f.Engine.InFlightExecutions); Assert.Empty(f.Host.Held);
        Assert.Contains("timer fault", f.Host.Error);
    }

    [Fact]
    public async Task R5_StoppedPlanBeforeFaultListenerStillReleasesAllLeases()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        var faultEntered = Barrier(); var allowFault = Barrier();
        f.Host.BeforeFaultHandling = () => { faultEntered.TrySetResult(); return allowFault.Task; };
        f.Time.FailNextChanges = 1;
        Assert.Throws<InvalidOperationException>(() => f.Engine.TakeOverride(Input, "owner", "test", TimeSpan.FromSeconds(1)));
        await Within(faultEntered.Task);
        try
        {
            await Within(f.Observer.Released.Reader.ReadAsync().AsTask());
            await Within(f.Observer.Released.Reader.ReadAsync().AsTask());
        }
        finally { allowFault.TrySetResult(); }
        await Within(f.Host.Cleanup);
        Assert.Empty(f.Host.Held); Assert.False(f.Engine.IsRunning);
    }

    [Fact]
    public async Task R5_CleanupSelectedBeforeReadyFaultStillReportsCurrentLifecycleFailure()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        var selected = Barrier(); var inspectFault = Barrier();
        // Let cleanup win first, then make the captured lifecycle's fault ready before inspection.
        f.Host.BeforeFaultHandling = () => { selected.TrySetResult(); return inspectFault.Task; };
        var stop = f.Host.StopAsync(TimeSpan.FromSeconds(5));
        await Within(selected.Task);
        try
        {
            await Within(f.Observer.Released.Reader.ReadAsync().AsTask());
            await Within(f.Observer.Released.Reader.ReadAsync().AsTask());
            Assert.Empty(f.Host.Held);
            f.Time.FailNextChanges = 1;
            Assert.Throws<InvalidOperationException>(() =>
                f.Scheduler.Schedule(f.Clock.MonotonicNow + TimeSpan.FromSeconds(1), () => { }));
        }
        finally { inspectFault.TrySetResult(); }
        Assert.True((await Within(stop)).Completed);
        Assert.Contains("Scheduler fault: timer fault", f.Host.Error);
        Assert.False(f.Engine.IsRunning); Assert.Equal(0, f.Scheduler.ActiveTimers);
    }

    [Fact]
    public async Task R6_CallbackReturnsWhileHostGateIsHeld()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        var locked = Barrier(); using var unlock = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (HostGate(f)) { locked.SetResult(); Assert.True(unlock.Wait(TimeSpan.FromSeconds(10))); } });
        await Within(locked.Task);
        try { await Within(Task.Run(() => f.Observer.Emit(Input, false))); }
        finally { unlock.Set(); await Within(holder); await f.Stop(); }
    }

    [Fact]
    public async Task R6_ExternalGuaranteeGetterRunsOutsideHostGate()
    {
        var f = new RuntimeFixture();
        f.Observer.ReadGuarantee = () => Assert.False(Monitor.IsEntered(HostGate(f)));
        await Within(f.Start()); await f.Stop();
    }

    [Fact]
    public async Task R7_DisposeClosesAdmissionBeforeCleanupContinuation()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        var commit = Barrier(); var allowCommit = Barrier();
        f.Host.BeforeDisposeCommit = () => { commit.TrySetResult(); return allowCommit.Task; };
        var dispose = f.Host.DisposeAsync(TimeSpan.FromSeconds(5)); await Within(commit.Task);
        try
        {
            Assert.Throws<ObjectDisposedException>(() => { _ = f.Host.StartAsync(_ => throw new InvalidOperationException("new lifecycle admitted")); });
        }
        finally { allowCommit.TrySetResult(); await Within(dispose); }
    }

    [Fact]
    public async Task RecoveredTimerFailureDoesNotStopNextLifecycleWithOldFault()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        f.Observer.Emit(Input, true); var call = await Within(f.Executor.NextAsync());
        f.Time.FailNextChanges = 1; call.Complete();
        await Within(f.Host.Cleanup);
        Assert.Contains("timer fault", f.Host.Error);
        f.Observer.Values[Input] = false;
        await Within(f.Start());
        Assert.True(f.Engine.IsRunning);
        Assert.Null(f.Host.Error);
        await f.Stop();
    }

    [Fact]
    public async Task ManualFlightReservesItsTransitiveObservationDependencies()
    {
        var f = new RuntimeFixture(); await Within(f.Start(cancellable: false));
        using var noise = f.Engine.Subscribe([Noise]).Subscription;
        f.Engine.RegisterCapability(Binding("output", cancellable: false, dependencies: [Noise]));
        while ((await Within(noise.ReadAsync().AsTask())).State!.Observation.Quality != StateQuality.Known) { }
        var result = f.Engine.SubmitManualCommand(new(new Guid(9000, 0, 0, new byte[8]), Output,
            StateValue.From(Schema, true), "owner", f.Clock.UtcNow + TimeSpan.FromSeconds(10), false));
        Assert.Equal(OutcomeStatus.Accepted, result.Status);
        var call = await Within(f.Executor.NextAsync());
        try
        {
            Assert.NotNull(call.Command);
            Assert.False(f.Engine.TryBeginObservationRelease(Noise, recovery: true));
            Assert.Contains(Noise, f.Host.Held);
            Assert.Equal(1, f.Engine.InFlightExecutions);
        }
        finally { call.Complete(); await Within(f.Engine.WaitForIdleAsync()); await f.Stop(); }
    }
}
