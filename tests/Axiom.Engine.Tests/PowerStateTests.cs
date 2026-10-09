using System.Collections.Immutable;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class PowerStateTests
{
    private static TaskInvocation Detection(string mode = "battery-below", string threshold = "30") => new(PowerStateCapabilities.State, 1,
        ImmutableDictionary<string, string>.Empty.Add("mode", mode).Add("threshold", threshold));

    [Fact]
    public void ConfigurationUsesCanonicalSharedResourcesAndRejectsInvalidThresholdsOrModes()
    {
        var compiler = new PowerStateTaskDetection();
        Assert.Equal("battery-below:30", Assert.Single(compiler.Compile(Detection(), ResourceScope.Machine).Dependencies).ResourceId);
        Assert.Equal("battery-below:1", PowerStateCapabilities.Validate(Detection(threshold: "01")));
        Assert.Equal("ac", PowerStateCapabilities.Validate(Detection("ac", "99")));
        Assert.Equal("battery", PowerStateCapabilities.Validate(Detection("battery", "1")));
        foreach (var threshold in new[] { "0", "100", "255", "-1", "1.5", "+20", " 20", "20 ", "" })
            Assert.Throws<ArgumentException>(() => compiler.Compile(Detection(threshold: threshold), ResourceScope.Machine));
        Assert.Throws<ArgumentException>(() => PowerStateCapabilities.Validate(Detection("unknown")));
        Assert.Throws<ArgumentException>(() => PowerStateCapabilities.Validate(Detection() with { SchemaVersion = 2 }));
        Assert.Throws<ArgumentException>(() => PowerStateCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.Add("extra", "x") }));
        foreach (var id in new[] { "battery-below:0", "battery-below:100", "battery-below:01", "battery-below:+20", "battery-below:20 ", "Battery", "" })
            Assert.False(PowerStateCapabilities.ValidResourceId(id));
    }

    [Theory]
    [InlineData(0, 1, 29, false, true, true)]
    [InlineData(0, 1, 30, false, true, false)]
    [InlineData(0, 1, 31, false, true, false)]
    [InlineData(1, 1, 5, true, false, false)]
    [InlineData(1, 128, 255, true, false, false)]
    [InlineData(0, 128, 5, false, null, null)]
    [InlineData(255, 1, 5, null, null, null)]
    [InlineData(2, 1, 5, null, null, null)]
    [InlineData(0, 255, 5, false, null, null)]
    [InlineData(0, 1, 255, false, true, null)]
    [InlineData(0, 1, 101, false, true, null)]
    public void KnownSupplyAndStrictBatteryThresholdRespectUnknownAndNoBattery(byte ac, byte flags, byte percent, bool? expectedAc, bool? expectedBattery, bool? expectedLow)
    {
        var status = new PowerStateSnapshot(ac, flags, percent);
        Assert.Equal(expectedAc, PowerStateState.Evaluate("ac", status));
        Assert.Equal(expectedBattery, PowerStateState.Evaluate("battery", status));
        Assert.Equal(expectedLow, PowerStateState.Evaluate("battery-below:30", status));
        Assert.Null(PowerStateState.Evaluate("ac", null));
    }

    [Fact]
    public async Task SavedPowerConditionCompilesWithDesiredKeepAwakeOrOneShotPowerAndBrightnessActions()
    {
        var plugin = new PowerStatePlugin(); var compiler = new AutomationTaskCompiler();
        compiler.Register(plugin.Detections.Single()); compiler.Register(new KeepAwakeTaskAction());
        var task = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "synthetic plugged-in work", true, Detection("ac"),
            new(KeepAwakeCapabilities.Hold, 1, ImmutableDictionary<string, string>.Empty.Add("target", "system")));
        var path = Path.Combine(Path.GetTempPath(), "axiom-power-state-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(task, default);
            using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync());
            Assert.Equal(task.Detection.Parameters, loaded.Detection.Parameters);
            var rule = compiler.Compile(loaded, "owner", ResourceScope.Session("owner", 1));
            Assert.Equal(RuleActionKind.Desired, rule.Action.Kind);
            Assert.Contains(new(PowerStateCapabilities.State, KeepAwakeCapabilities.Hold), plugin.Combinations);
            Assert.Contains(new(PowerStateCapabilities.State, PowerSchemeCapabilities.Set), plugin.Combinations);
            Assert.Contains(new(PowerStateCapabilities.State, BuiltInBrightnessCapabilities.Set), plugin.Combinations);
            Assert.DoesNotContain(plugin.Combinations, c => c.ActionId == SessionLockCapabilities.Lock);
            Assert.True(plugin.Bindings.Single().Descriptor.Observe); Assert.False(plugin.Bindings.Single().Descriptor.Command);
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact(Timeout = 10000)]
    public async Task SharedQueriesCoalesceOnlyNotificationsAndDoNotRepeatSatisfiedValues()
    {
        var hints = Channel.CreateBounded<string>(128); var updates = Channel.CreateBounded<(PowerStateValue[] Values, bool Snapshot)>(8);
        using var stop = new CancellationTokenSource(); var queries = 0;
        hints.Writer.TryWrite("locked");
        for (var i = 0; i < 64; i++) hints.Writer.TryWrite("power-state-changed");
        var run = PowerStateObservation.RunAsync(["ac", "battery", "battery-below:30", "battery-below:10"], hints.Reader,
            () => { queries++; return new(0, 1, queries == 1 ? (byte)30 : (byte)29); }, (values, baseline) => Assert.True(updates.Writer.TryWrite((values, baseline))), stop.Token);
        var initial = await updates.Reader.ReadAsync(); Assert.True(initial.Snapshot); Assert.False(initial.Values[2].Satisfied);
        var changed = await updates.Reader.ReadAsync(); Assert.False(changed.Snapshot); Assert.True(changed.Values[2].Satisfied);
        Assert.False(changed.Values[3].Satisfied); Assert.Equal(2, queries); Assert.False(updates.Reader.TryRead(out _));
        hints.Writer.TryWrite("power-state-changed"); // still below threshold; no duplicate provider update
        hints.Writer.TryComplete(); await Assert.ThrowsAsync<IOException>(() => run);
        Assert.Equal(3, queries); Assert.False(updates.Reader.TryRead(out _));
    }

    [Fact(Timeout = 10000)]
    public async Task QueryFailureAndUnknownPercentageBecomeUnknownAndRecoveryOrResumeBuildsBaseline()
    {
        var hints = Channel.CreateBounded<string>(64); var updates = Channel.CreateBounded<(PowerStateValue[] Values, bool Snapshot)>(8);
        using var stop = new CancellationTokenSource(); var current = new PowerStateSnapshot(0, 1, 40); var fail = false;
        var run = PowerStateObservation.RunAsync(["ac", "battery-below:30"], hints.Reader,
            () => fail ? throw new IOException("synthetic query failure") : current, (values, baseline) => Assert.True(updates.Writer.TryWrite((values, baseline))), stop.Token);
        Assert.True((await updates.Reader.ReadAsync()).Snapshot);
        fail = true; hints.Writer.TryWrite("power-state-changed"); var unknown = await updates.Reader.ReadAsync();
        Assert.False(unknown.Snapshot); Assert.All(unknown.Values, s => Assert.Null(s.Satisfied));
        fail = false; current = new(0, 1, 20); hints.Writer.TryWrite("power-state-changed"); var recovered = await updates.Reader.ReadAsync();
        Assert.True(recovered.Snapshot); Assert.True(recovered.Values[1].Satisfied);
        current = new(0, 1, 255); hints.Writer.TryWrite("power-state-changed"); var unknownPercent = await updates.Reader.ReadAsync();
        Assert.False(unknownPercent.Snapshot); Assert.False(unknownPercent.Values[0].Satisfied); Assert.Null(unknownPercent.Values[1].Satisfied);
        current = new(0, 1, 10); hints.Writer.TryWrite("power-state-changed"); Assert.True((await updates.Reader.ReadAsync()).Snapshot);
        current = new(1, 1, 10); hints.Writer.TryWrite("resumed"); var resume = await updates.Reader.ReadAsync();
        Assert.True(resume.Snapshot); Assert.True(resume.Values[0].Satisfied); Assert.False(resume.Values[1].Satisfied);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact(Timeout = 10000)]
    public async Task LostHintsDuringQueryCannotPublishNewKnownValues()
    {
        var hints = Channel.CreateBounded<string>(64); var updates = new List<PowerStateValue[]>(); var queries = 0; var failed = false;
        var run = PowerStateObservation.RunAsync(["ac"], hints.Reader, () => { queries++; if (queries == 2) failed = true; return new(1, 1, 80); },
            (values, _) => updates.Add(values), default, () => failed);
        hints.Writer.TryWrite("power-state-changed"); await Assert.ThrowsAsync<IOException>(() => run);
        Assert.Single(updates); Assert.Equal(2, queries);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderNotificationFailureIsUnknownAndIsolatedUntilCancellation(bool afterSnapshot)
    {
        var updates = Channel.CreateBounded<ProviderUpdate>(4); using var stop = new CancellationTokenSource();
        var provider = new PowerStateCapabilityProvider((ids, publish, token) =>
        {
            if (afterSnapshot) publish([new(ids[0], true)], true);
            throw new IOException("synthetic notification failure");
        });
        var run = provider.ObserveAsync([new(PowerStateCapabilities.State, "ac")], u => Assert.True(updates.Writer.TryWrite(u)), stop.Token);
        if (afterSnapshot) Assert.True((await updates.Reader.ReadAsync()).Snapshot);
        var unknown = await updates.Reader.ReadAsync(); Assert.Equal(!afterSnapshot, unknown.Snapshot);
        Assert.Equal(StateQuality.Unknown, Assert.Single(unknown.Samples).Quality); Assert.Null(unknown.Samples[0].Boolean); Assert.False(run.IsCompleted);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ObserveAsync([new(PowerStateCapabilities.State, "battery-below:00")], _ => { }, default));
    }

    [Fact(Timeout = 10000)]
    public async Task InitialPluggedInDesiredStateAppliesAndReleasesForBatteryUnknownPauseOrStop()
    {
        var executor = new SyntheticDesiredExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(PowerStateCapabilities.State, schema: schema));
        var condition = new PowerStateTaskDetection().Compile(Detection("ac"), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-power-desired", [key]); long sequence = 0;
        void Set(PowerStateSnapshot? status, bool baseline = false)
        {
            var value = PowerStateState.Evaluate(key.ResourceId, status);
            var observation = new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        var rule = new RuleDefinition("synthetic-plugged-in", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Set(new(1, 1, 80), true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution();
        await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized); Assert.Equal(1, executor.Applies);
        Set(new(0, 1, 80)); Assert.False(executor.Authorized);
        Set(new(1, 1, 80)); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        Set(null); Assert.False(executor.Authorized);
        Set(new(1, 1, 80), true); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", rule.Scope, condition, rule.Action, enabled: false)); Assert.False(executor.Authorized);
        fixture.Engine.SetRule(new(rule.Id, 3, "owner", rule.Scope, condition, rule.Action)); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        fixture.Engine.Stop(); Assert.False(executor.Authorized);
    }

    private sealed class SyntheticDesiredExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public bool Authorized; public int Applies;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) => Authorized = authorized;
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token)
        { Applies++; return ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic request", Guid.NewGuid())); }
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException("Desired must remain Desired.");
    }

    [Fact(Timeout = 10000)]
    public async Task EngineOnlyTriggersNewLowBatteryEdgesAndNeverReplaysInitialRecoveryOrPause()
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(PowerStateCapabilities.State, schema: schema));
        var condition = new PowerStateTaskDetection().Compile(Detection(), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-power-state", [key]); long sequence = 0;
        var rule = new RuleDefinition("synthetic-power-rule", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        void Set(PowerStateSnapshot? status, bool baseline = false)
        {
            var value = PowerStateState.Evaluate(key.ResourceId, status);
            var observation = new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        Set(new(0, 1, 10), true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(new(0, 1, 30)); Set(new(0, 1, 29)); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(new(0, 1, 28)); Set(null); Set(new(0, 1, 20), true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", rule.Scope, condition, rule.Action, enabled: false)); Set(new(1, 1, 20)); Set(new(0, 1, 20));
        fixture.Engine.SetRule(new(rule.Id, 3, "owner", rule.Scope, condition, rule.Action)); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(new(1, 1, 20)); Set(new(0, 1, 20)); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
    }
}
