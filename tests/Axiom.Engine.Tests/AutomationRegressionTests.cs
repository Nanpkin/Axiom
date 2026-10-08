using Axiom.Domain;
using static Axiom.Engine.Tests.AutomationFixture;

namespace Axiom.Engine.Tests;

public sealed class AutomationRegressionTests
{
    [Fact]
    public void WeakerObserverGuaranteesCannotConfirmLightweight()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule(timing: new(maximumInputAge: TimeSpan.FromMilliseconds(100))));
        f.Engine.RequestMode(EngineMode.Lightweight);
        var plan = f.Engine.GetObservationPlan();
        var weak = plan.Requirements.ToDictionary();
        weak[Input] = new(TimeSpan.FromSeconds(1), false);
        Assert.False(f.Engine.AcknowledgeObservationPlan(plan.Version, plan.References.Keys, guarantees: weak));
        Assert.Equal(TransitionStatus.Failed, f.Engine.CurrentMode.Status);
        Assert.Equal(EngineMode.Normal, f.Engine.CurrentMode.Effective);
        Assert.Contains("guarantee", f.Engine.CurrentMode.Reason);
    }

    [Fact]
    public void CapabilitySchemaAndFidelityChangesCannotReuseOldRuleAssumptions()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule()); f.Update(true);
        f.Engine.RegisterCapability(Binding("input", schema: new("bool", 2)));
        Assert.Equal(TruthValue.Unknown, f.Engine.GetRuleEvaluation("rule").Value);
        Assert.Equal("InputCapabilityContractChanged", f.Engine.GetRuleEvaluation("rule").Reason);
        f.Engine.RegisterCapability(Binding("input", fidelity: EventFidelity.LatestValue));
        Assert.Throws<ArgumentException>(() => f.Engine.SetRule(f.Rule("edge", command: true)));
    }

    [Fact]
    public void StopPublishesObserverReleaseAndOverrideSurvivesSameObjectRestart()
    {
        using var f = new AutomationFixture();
        var observers = new FakeObservers();
        f.Engine.SetRule(f.Rule()); f.Engine.TakeOverride(Output, "owner", "manual"); observers.Apply(f.Engine);
        f.Engine.Stop();
        Assert.Equal(2, f.Engine.GetObservationPlan().Release.Length);
        observers.Apply(f.Engine);
        Assert.Empty(observers.Held);
        f.Engine.Start(); f.Engine.ConfigureAutomation(f.Authorization, new SequentialIds());
        f.Engine.RegisterCapability(Binding("input")); f.Engine.RegisterCapability(Binding("output"));
        f.Engine.SetRule(f.Rule());
        Assert.Equal(DecisionStatus.Overridden, f.Engine.GetDecision(Output).Status);
        Assert.Equal(0, f.Scheduler.Pending);
    }

    [Fact(Timeout = 10000)]
    public async Task ManualCommandsAcquireOverrideAndDuplicateIdIsNotExecutedTwice()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        f.Engine.SetRule(f.Rule()); f.Engine.StartExecution();
        var command = new Command(new Guid(42, 0, 0, new byte[8]), Output, StateValue.From(Schema, true), "owner", f.Clock.UtcNow.AddSeconds(10), false);
        Assert.Equal(OutcomeStatus.Accepted, f.Engine.SubmitManualCommand(command).Status);
        var call = await executor.NextAsync(); call.Complete();
        await f.Engine.WaitForIdleAsync();
        Assert.Equal(DecisionStatus.Overridden, f.Engine.GetDecision(Output).Status);
        Assert.Equal(OutcomeStatus.Rejected, f.Engine.SubmitManualCommand(command).Status);
        Assert.Equal(1, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task ExecutorExceptionDoesNotInventSuccessOrStartRetryLoop()
    {
        using var f = new AutomationFixture(new ThrowingExecutor());
        f.Engine.SetRule(f.Rule(command: true)); f.Engine.StartExecution(); f.Update(true);
        await f.Engine.WaitForIdleAsync();
        Assert.Equal(OutcomeStatus.Unknown, f.Engine.GetDecision(Output).LastOutcome!.Status);
        Assert.Equal(0, f.Engine.PendingDeadlines);
        f.Scheduler.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, f.Engine.InFlightExecutions);
    }

    [Fact(Timeout = 10000)]
    public async Task IndependentResourcesRespectGlobalConcurrency()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        for (var i = 0; i < 6; i++) f.Engine.SetRule(f.Rule($"r{i}", command: true, output: new("output", $"r{i}", ResourceScope.Machine)));
        f.Engine.StartExecution(new(concurrency: 2, queueCapacity: 8)); f.Update(true);
        var first = await executor.NextAsync(); var second = await executor.NextAsync();
        Assert.Equal(2, f.Engine.InFlightExecutions);
        Assert.NotEqual(first.Command!.Key, second.Command!.Key);
        first.Complete(); second.Complete();
        for (var i = 0; i < 4; i++) { var call = await executor.NextAsync(); Assert.InRange(f.Engine.InFlightExecutions, 1, 2); call.Complete(); }
        await f.Engine.WaitForIdleAsync();
        Assert.Equal(6, executor.Count);
        Assert.Equal(0, f.Engine.PendingDeadlines);
    }

    [Fact(Timeout = 10000)]
    public async Task SettingApplyIntervalIsADeadlineNotARepeatedCheck()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        f.Engine.RegisterCapability(Binding("output", interval: TimeSpan.FromSeconds(5)));
        f.Engine.SetRule(f.Rule()); f.Engine.StartExecution(); f.Update(true);
        var first = await executor.NextAsync(); first.Complete(); await f.Engine.WaitForIdleAsync();
        f.Update(true, Output); f.Update(false, Output);
        Assert.Equal(1, executor.Count);
        Assert.Equal(1, f.Scheduler.Pending);
        f.Scheduler.Advance(TimeSpan.FromSeconds(5));
        var second = await executor.NextAsync(); second.Complete(); await f.Engine.WaitForIdleAsync();
        f.Update(true, Output);
        Assert.Equal(0, f.Scheduler.Pending);
    }

    [Fact]
    public void RetainedRuleTraceIsIdenticalAcrossModesIncludingTiming()
    {
        static string[] Replay(bool lightweight)
        {
            using var f = new AutomationFixture();
            var observers = new FakeObservers();
            f.Engine.SetRule(f.Rule("keep", priority: -100, timing: new(minimumDuration: TimeSpan.FromSeconds(2))));
            f.Engine.SetRule(f.Rule("optional", keep: false, output: Noise));
            observers.Apply(f.Engine);
            if (lightweight) { f.Engine.RequestMode(EngineMode.Lightweight); observers.Apply(f.Engine); }
            var trace = new List<string>();
            void Record() => trace.Add($"{f.Engine.GetRuleEvaluation("keep").Value}|{f.Engine.GetDecision(Output).Action}|{f.Engine.GetDecision(Output).Status}");
            f.Update(true); Record(); f.Scheduler.Advance(TimeSpan.FromSeconds(1)); Record();
            f.Update(false); Record(); f.Update(true); Record(); f.Scheduler.Advance(TimeSpan.FromSeconds(2)); Record();
            f.Engine.Update(f.Source, ++f.Sequence, new(Input, StateQuality.Unknown, null, f.Clock.UtcNow)); Record();
            return trace.ToArray();
        }
        Assert.Equal(Replay(false), Replay(true));
        Assert.Equal(Replay(true), Replay(true));
    }

    [Fact]
    public void LateCancelledSchedulerCallbackCannotReactivateRule()
    {
        var clock = new FakeClock(); var scheduler = new LateScheduler();
        using var engine = new EngineHost(clock, scheduler, new FakeExecutor());
        engine.Start(); engine.ConfigureAutomation(new FakeAuthorization(), new SequentialIds());
        engine.RegisterCapability(Binding("input")); engine.RegisterCapability(Binding("output"));
        var source = engine.RegisterSource("s", [Input, Output]);
        engine.ApplySnapshot(source, 0, [Observe(Input, false), Observe(Output, false)]);
        engine.SetRule(new("r", 1, "owner", ResourceScope.Machine, RuleCondition.Equal(Input, StateValue.From(Schema, true)),
            RuleAction.Desired(Output, StateValue.From(Schema, true)), timing: new(minimumDuration: TimeSpan.FromSeconds(2))));
        engine.Update(source, 1, Observe(Input, true));
        var stale = scheduler.Last!;
        engine.Update(source, 2, Observe(Input, false)); clock.Advance(TimeSpan.FromSeconds(3)); stale();
        Assert.Equal(TruthValue.False, engine.GetRuleEvaluation("r").Value);
        Assert.Equal(0, engine.PendingDeadlines);
    }

    private sealed class ThrowingExecutor : ICapabilityExecutor
    {
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken cancellationToken) => throw new InvalidOperationException("fake");
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken cancellationToken) => throw new InvalidOperationException("fake");
    }
    private sealed class LateScheduler : IDeadlineScheduler
    {
        internal Action? Last;
        public IDisposable Schedule(TimeSpan deadline, Action callback) { Last = callback; return new Lease(); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
}
