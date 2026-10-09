using System.Collections.Immutable;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class DisplayCountTests
{
    private static TaskInvocation Detection(string comparison = "at-least", string count = "2") => new(DisplayCountCapabilities.Count, 1,
        ImmutableDictionary<string, string>.Empty.Add("comparison", comparison).Add("count", count));
    [Fact]
    public async Task SavedCountConditionsCompileToCanonicalBooleanResourcesAndRejectUnknownParameters()
    {
        var plugin = new DisplayCountPlugin(); var compiler = new AutomationTaskCompiler();
        compiler.Register(plugin.Detections.Single()); compiler.Register(new KeepAwakeTaskAction());
        var task = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "synthetic multi-display work", true, Detection(count: "02"),
            new(KeepAwakeCapabilities.Hold, 1, ImmutableDictionary<string, string>.Empty.Add("target", "system")));
        var path = Path.Combine(Path.GetTempPath(), "axiom-display-count-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(task, default);
            using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync());
            var rule = compiler.Compile(loaded, "owner", ResourceScope.Session("owner", 1));
            Assert.Equal(RuleActionKind.Desired, rule.Action.Kind); Assert.Equal("at-least:2", Assert.Single(rule.Condition.Dependencies).ResourceId);
            Assert.Equal("exact:1", DisplayCountCapabilities.Validate(Detection("exact", "1")));
            Assert.Contains(new(DisplayCountCapabilities.Count, WindowMonitorCapabilities.Move), plugin.Combinations);
            Assert.Contains(new(DisplayCountCapabilities.Count, "program.start"), plugin.Combinations);
            Assert.True(plugin.Bindings.Single().Descriptor.Observe); Assert.False(plugin.Bindings.Single().Descriptor.Command);
            foreach (var value in new[] { "0", "17", "65", "-1", "+2", "2.0", " 2", "2 ", "002", "" })
                Assert.Throws<ArgumentException>(() => DisplayCountCapabilities.Validate(Detection(count: value)));
            Assert.Throws<ArgumentException>(() => DisplayCountCapabilities.Validate(Detection("not-equal")));
            Assert.Throws<ArgumentException>(() => DisplayCountCapabilities.Validate(Detection() with { SchemaVersion = 2 }));
            Assert.Throws<ArgumentException>(() => DisplayCountCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.Add("deviceId", "synthetic") }));
            foreach (var id in new[] { "exact:0", "exact:17", "exact:01", "at-least:+2", "at-least:2 ", "monitor-id", "" }) Assert.False(DisplayCountCapabilities.ValidResourceId(id));
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(2, true, false)]
    [InlineData(3, true, false)]
    [InlineData(64, true, false)]
    [InlineData(0, null, null)]
    [InlineData(65, null, null)]
    [InlineData(-1, null, null)]
    [InlineData(null, null, null)]
    public void LogicalCountUsesAtLeastOrExactAndNeverTreatsFailedEmptyOrOverflowQueriesAsSingleDisplay(int? count, bool? multiple, bool? single)
    {
        Assert.Equal(multiple, DisplayCountState.Evaluate("at-least:2", count));
        Assert.Equal(single, DisplayCountState.Evaluate("exact:1", count));
        Assert.Null(DisplayCountState.ConfirmCount(2, false)); Assert.Null(DisplayCountState.ConfirmCount(0, true)); Assert.Null(DisplayCountState.ConfirmCount(65, true));
        Assert.Equal(64, DisplayCountState.ConfirmCount(64, true));
    }

    [Fact(Timeout = 10000)]
    public async Task OneQueryServesAllConditionsCoalescesHintsAndDeduplicatesSatisfiedValues()
    {
        var hints = Channel.CreateBounded<string>(128); var updates = Channel.CreateBounded<(DisplayCountValue[] Values, bool Baseline)>(8);
        using var stop = new CancellationTokenSource(); var queries = 0;
        hints.Writer.TryWrite("locked"); for (var i = 0; i < 64; i++) hints.Writer.TryWrite("display-changed");
        var run = DisplayCountObservation.RunAsync(["at-least:2", "exact:1", "exact:2"], hints.Reader, () => ++queries == 1 ? 1 : 2,
            (values, baseline) => Assert.True(updates.Writer.TryWrite((values, baseline))), stop.Token);
        var initial = await updates.Reader.ReadAsync(); Assert.True(initial.Baseline); Assert.False(initial.Values[0].Satisfied);
        var changed = await updates.Reader.ReadAsync(); Assert.False(changed.Baseline); Assert.True(changed.Values[0].Satisfied); Assert.True(changed.Values[2].Satisfied);
        Assert.Equal(2, queries); Assert.False(updates.Reader.TryRead(out _));
        hints.Writer.TryWrite("display-changed"); hints.Writer.TryComplete(); await Assert.ThrowsAsync<IOException>(() => run);
        Assert.Equal(3, queries); Assert.False(updates.Reader.TryRead(out _));
    }

    [Fact(Timeout = 10000)]
    public async Task QueryFailureAndRecoveryAndResumeAreSafeBaselinesWithoutRetryTimers()
    {
        var hints = Channel.CreateBounded<string>(64); var updates = Channel.CreateBounded<(DisplayCountValue[] Values, bool Baseline)>(8);
        using var stop = new CancellationTokenSource(); var count = 2; var fail = false;
        var run = DisplayCountObservation.RunAsync(["at-least:2", "exact:1"], hints.Reader, () => fail ? throw new IOException("synthetic query failure") : count,
            (values, baseline) => Assert.True(updates.Writer.TryWrite((values, baseline))), stop.Token);
        Assert.True((await updates.Reader.ReadAsync()).Baseline);
        fail = true; hints.Writer.TryWrite("display-changed"); var unknown = await updates.Reader.ReadAsync();
        Assert.False(unknown.Baseline); Assert.All(unknown.Values, s => Assert.Null(s.Satisfied));
        fail = false; count = 1; hints.Writer.TryWrite("display-changed"); var restored = await updates.Reader.ReadAsync(); Assert.True(restored.Baseline); Assert.True(restored.Values[1].Satisfied);
        count = 2; hints.Writer.TryWrite("display-changed"); Assert.False((await updates.Reader.ReadAsync()).Baseline);
        count = 1; hints.Writer.TryWrite("resumed"); Assert.True((await updates.Reader.ReadAsync()).Baseline);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact(Timeout = 10000)]
    public async Task OverflowDuringQueryCannotPublishANewKnownCount()
    {
        var hints = Channel.CreateBounded<string>(64); var samples = new List<DisplayCountValue[]>(); var queries = 0; var failed = false;
        var run = DisplayCountObservation.RunAsync(["exact:1"], hints.Reader, () => { queries++; if (queries == 2) failed = true; return 1; },
            (values, _) => samples.Add(values), default, () => failed);
        hints.Writer.TryWrite("display-changed"); await Assert.ThrowsAsync<IOException>(() => run); Assert.Single(samples); Assert.Equal(2, queries);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureIsUnknownAndKeepsItsStreamAliveUntilCancellation(bool afterSnapshot)
    {
        var updates = Channel.CreateBounded<ProviderUpdate>(4); using var stop = new CancellationTokenSource();
        var provider = new DisplayCountCapabilityProvider((ids, publish, _) =>
        {
            if (afterSnapshot) publish([new(ids[0], true)], true);
            throw new IOException("synthetic notification failure");
        });
        var run = provider.ObserveAsync([new(DisplayCountCapabilities.Count, "exact:1")], u => Assert.True(updates.Writer.TryWrite(u)), stop.Token);
        if (afterSnapshot) Assert.True((await updates.Reader.ReadAsync()).Snapshot);
        var unknown = await updates.Reader.ReadAsync(); Assert.Equal(!afterSnapshot, unknown.Snapshot); Assert.Equal(StateQuality.Unknown, Assert.Single(unknown.Samples).Quality);
        Assert.Null(unknown.Samples[0].Boolean); Assert.False(run.IsCompleted);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ObserveAsync([new(DisplayCountCapabilities.Count, "exact:01")], _ => { }, default));
    }

    [Theory(Timeout = 10000)]
    [InlineData("at-least", "2", 2, 1)]
    [InlineData("exact", "1", 1, 2)]
    public async Task CommandsOnlyUseReliableNewEdgesNeverInitialRecoveryResumeOrReenable(string comparison, string count, int matching, int other)
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(DisplayCountCapabilities.Count, schema: schema));
        var condition = new DisplayCountTaskDetection().Compile(Detection(comparison, count), ResourceScope.Machine); var key = condition.Key!;
        var source = fixture.Engine.RegisterSource("synthetic-display", [key]); long sequence = 0;
        void Set(int? value, bool baseline = false)
        {
            var known = DisplayCountState.Evaluate(key.ResourceId, value);
            var observation = new Observation(key, known.HasValue ? StateQuality.Known : StateQuality.Unknown, known.HasValue ? StateValue.From(schema, known.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        var rule = new RuleDefinition("synthetic-display-command", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Set(matching, true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(other); Set(matching); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(null); Set(matching, true); Set(other, true); Set(matching, true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new(rule.Id, 2, rule.Principal, rule.Scope, condition, rule.Action, enabled: false)); Set(other); Set(matching);
        fixture.Engine.SetRule(new(rule.Id, 3, rule.Principal, rule.Scope, condition, rule.Action)); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(other); Set(matching); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task InitialMultiDisplayConditionAuthorizesKeepAwakeAndSingleUnknownOrDisconnectReleases()
    {
        var executor = new LeaseExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(DisplayCountCapabilities.Count, schema: schema));
        var condition = new DisplayCountTaskDetection().Compile(Detection(), ResourceScope.Machine); var key = condition.Key!;
        var source = fixture.Engine.RegisterSource("synthetic-display-desired", [key]); long sequence = 0;
        void Set(int? count, bool baseline = false)
        {
            var known = DisplayCountState.Evaluate(key.ResourceId, count);
            var observation = new Observation(key, known.HasValue ? StateQuality.Known : StateQuality.Unknown, known.HasValue ? StateValue.From(schema, known.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        var rule = new RuleDefinition("synthetic-multi-awake", 1, "owner", ResourceScope.Machine, condition, RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Set(2, true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        fixture.Update(true, AutomationFixture.Output); Set(1); Assert.False(executor.Authorized);
        Set(2); Assert.True(executor.Authorized); Set(null); Assert.False(executor.Authorized);
        Set(2, true); Assert.True(executor.Authorized); fixture.Engine.Disconnect(source); Assert.False(executor.Authorized);
    }
    private sealed class LeaseExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public bool Authorized;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) => Authorized = authorized;
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic lease", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException("Unexpected command.");
    }
}
