using Axiom.Domain;
using static Axiom.Engine.Tests.AutomationFixture;

namespace Axiom.Engine.Tests;

internal sealed class FakeObservers
{
    internal readonly HashSet<ResourceKey> Held = [];
    internal int Acquired;
    internal int Released;
    internal void Apply(EngineHost engine, bool failRelease = false)
    {
        var plan = engine.GetObservationPlan();
        foreach (var key in plan.Acquire) if (Held.Add(key)) Acquired++;
        if (!failRelease) foreach (var key in plan.Release) if (Held.Remove(key)) Released++;
        engine.AcknowledgeObservationPlan(plan.Version, Held, failRelease ? "fake release failed" : null, plan.Requirements);
    }
}

public sealed class ModeTests
{
    [Fact]
    public void LowPriorityRetainedRuleKeepsSharedObserversAndOptionalResourcesRelease()
    {
        using var f = new AutomationFixture();
        var observers = new FakeObservers();
        f.Engine.SetRule(f.Rule("keep", priority: -100));
        f.Engine.SetRule(f.Rule("pause", priority: 100, keep: false, output: Noise));
        observers.Apply(f.Engine);
        Assert.Equal(2, f.Engine.GetObservationPlan().References[Input]);
        f.Update(true);
        var retainedCount = f.Engine.GetRuleEvaluation("keep").EvaluationCount;
        f.Engine.RequestMode(EngineMode.Lightweight);
        Assert.Equal(TransitionStatus.Pending, f.Engine.CurrentMode.Status);
        var plan = f.Engine.GetObservationPlan();
        Assert.Contains(Noise, plan.Release);
        Assert.DoesNotContain(Input, plan.Release);
        Assert.Equal(1, plan.References[Input]);
        observers.Apply(f.Engine);
        Assert.Equal(EngineMode.Lightweight, f.Engine.CurrentMode.Effective);
        Assert.Equal(retainedCount, f.Engine.GetRuleEvaluation("keep").EvaluationCount);
        Assert.Equal(TruthValue.True, f.Engine.GetRuleEvaluation("keep").Value);
        Assert.Equal(TruthValue.Unknown, f.Engine.GetRuleEvaluation("pause").Value);
        Assert.Equal(0, f.Engine.PendingDeadlines);
        var pausedCount = f.Engine.GetRuleEvaluation("pause").EvaluationCount;
        f.Update(false);
        Assert.Equal(pausedCount, f.Engine.GetRuleEvaluation("pause").EvaluationCount);
        Assert.Equal(1, observers.Released);
    }

    [Fact(Timeout = 10000)]
    public async Task ResumeRequiresMissingSnapshotAndDoesNotReplayCommands()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        var observers = new FakeObservers();
        f.Engine.SetRule(f.Rule(command: true, keep: false));
        observers.Apply(f.Engine);
        f.Engine.StartExecution();
        f.Engine.RequestMode(EngineMode.Lightweight); observers.Apply(f.Engine);
        Assert.Empty(observers.Held);
        f.Update(true);
        f.Engine.RequestMode(EngineMode.Normal); observers.Apply(f.Engine);
        Assert.Equal(TransitionStatus.Pending, f.Engine.CurrentMode.Status);
        f.Engine.ApplySnapshot(f.Source, ++f.Sequence, [Observe(Input, true), Observe(Output, false), Observe(Noise, false)]);
        observers.Apply(f.Engine);
        Assert.Equal(EngineMode.Normal, f.Engine.CurrentMode.Effective);
        await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        f.Update(false); f.Update(true);
        var call = await executor.NextAsync(); call.Complete();
        await f.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task InFlightOptionalActionRetainsDependenciesThenNeedsFreshObservation()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        var observers = new FakeObservers();
        f.Engine.RegisterCapability(Binding("output", cancellable: false));
        f.Engine.SetRule(f.Rule(keep: false)); observers.Apply(f.Engine);
        f.Engine.StartExecution(); f.Update(true);
        var call = await executor.NextAsync();
        f.Engine.RequestMode(EngineMode.Lightweight); observers.Apply(f.Engine);
        Assert.Equal(TransitionStatus.Pending, f.Engine.CurrentMode.Status);
        Assert.Contains(Input, observers.Held);
        Assert.Contains(Output, observers.Held);
        call.Complete(); await f.Engine.WaitForIdleAsync();
        observers.Apply(f.Engine);
        Assert.Equal(TransitionStatus.Pending, f.Engine.CurrentMode.Status);
        f.Update(true, Output); observers.Apply(f.Engine);
        Assert.Equal(TransitionStatus.Effective, f.Engine.CurrentMode.Status);
        Assert.Empty(observers.Held);
    }

    [Fact]
    public void ReleaseFailureRetainsOldModeAndReportsActualResidualResources()
    {
        using var f = new AutomationFixture();
        var observers = new FakeObservers();
        f.Engine.SetRule(f.Rule(keep: false)); observers.Apply(f.Engine);
        f.Engine.RequestMode(EngineMode.Lightweight); observers.Apply(f.Engine, failRelease: true);
        Assert.Equal(TransitionStatus.Failed, f.Engine.CurrentMode.Status);
        Assert.Equal(EngineMode.Normal, f.Engine.CurrentMode.Effective);
        Assert.Contains("fake release failed", f.Engine.CurrentMode.Reason);
        Assert.NotEmpty(f.Engine.CurrentMode.Exceptions);
        Assert.Contains(Input, observers.Held);
        Assert.Equal(0, f.Engine.PendingDeadlines);
    }

    [Fact]
    public void NecessaryExceptionsAndOverrideDependenciesRemainVisible()
    {
        using var f = new AutomationFixture();
        var observers = new FakeObservers();
        f.Engine.RegisterCapability(Binding("output", lightweight: false, dependencies: [Noise]));
        f.Engine.SetRule(f.Rule(priority: -100)); observers.Apply(f.Engine);
        f.Engine.TakeOverride(Output, "owner", "manual");
        f.Engine.RequestMode(EngineMode.Lightweight); observers.Apply(f.Engine);
        Assert.Equal(TransitionStatus.Effective, f.Engine.CurrentMode.Status);
        Assert.Contains(f.Engine.CurrentMode.Exceptions, x => x.Contains("normal-profile"));
        Assert.Contains(Noise, observers.Held);
        Assert.Equal(DecisionStatus.Overridden, f.Engine.GetDecision(Output).Status);
        Assert.Equal(2, f.Engine.GetObservationPlan().References[Output]);
    }

    [Fact]
    public void StaleAckCannotConfirmNewPlanAndTimeoutDoesNotClaimSuccess()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule());
        f.Engine.RequestMode(EngineMode.Lightweight);
        var old = f.Engine.GetObservationPlan();
        f.Engine.SetRule(f.Rule("other", output: Noise));
        Assert.False(f.Engine.AcknowledgeObservationPlan(old.Version, old.References.Keys));
        Assert.Equal(TransitionStatus.Pending, f.Engine.CurrentMode.Status);
        Assert.Contains(Noise, f.Engine.GetObservationPlan().Acquire);
        f.Scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TransitionStatus.Failed, f.Engine.CurrentMode.Status);
        Assert.Equal(EngineMode.Normal, f.Engine.CurrentMode.Effective);
        Assert.Equal(0, f.Engine.PendingDeadlines);
    }

    [Fact]
    public void RepeatedModeSwitchesDoNotLeakTimersOrDuplicateSharedAcquisitions()
    {
        using var f = new AutomationFixture();
        var observers = new FakeObservers();
        f.Engine.SetRule(f.Rule()); observers.Apply(f.Engine);
        var initialAcquired = observers.Acquired;
        for (var i = 0; i < 20; i++)
        {
            f.Engine.RequestMode(i % 2 == 0 ? EngineMode.Lightweight : EngineMode.Normal);
            observers.Apply(f.Engine);
            Assert.Equal(TransitionStatus.Effective, f.Engine.CurrentMode.Status);
            Assert.Equal(0, f.Scheduler.Pending);
        }
        Assert.Equal(initialAcquired, observers.Acquired);
        Assert.Equal(0, observers.Released);
    }

    [Fact]
    public void CancelledResumeDoesNotLeaveSnapshotRequirementsBlockingLightweight()
    {
        using var f = new AutomationFixture();
        var observers = new FakeObservers();
        f.Engine.SetRule(f.Rule(keep: false)); observers.Apply(f.Engine);
        f.Engine.RequestMode(EngineMode.Lightweight); observers.Apply(f.Engine);
        f.Engine.RequestMode(EngineMode.Normal); observers.Apply(f.Engine);
        Assert.Equal(TransitionStatus.Pending, f.Engine.CurrentMode.Status);
        f.Engine.RequestMode(EngineMode.Lightweight); observers.Apply(f.Engine);
        Assert.Equal(TransitionStatus.Effective, f.Engine.CurrentMode.Status);
        Assert.Equal(0, f.Scheduler.Pending);
    }
}
