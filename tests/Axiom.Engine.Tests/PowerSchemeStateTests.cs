using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class PowerSchemeStateTests
{
    private static TaskInvocation Detection(string scheme = "saver") => new(PowerSchemeStateCapabilities.State, 1,
        ImmutableDictionary<string, string>.Empty.Add("scheme", scheme));

    [Fact]
    public async Task RegistryPausedTemplateSaveAndStrictImportUseStandardSchemeWithoutFeedbackAction()
    {
        var modules = BuiltInAutomationPlugins.Create(); var plugin = Assert.Single(modules.OfType<PowerSchemeStatePlugin>());
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        Assert.DoesNotContain(plugin.Combinations, c => c.ActionId == PowerSchemeCapabilities.Set);
        Assert.Contains(plugin.Combinations, c => c.ActionId == KeepAwakeCapabilities.Hold);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "standard-saver-dim-panel"); Assert.True(template.Draft.Paused);
        var compiler = new AutomationTaskCompiler();
        foreach (var m in modules) { foreach (var d in m.Detections) compiler.Register(d); foreach (var a in m.Actions) compiler.Register(a); }
        Assert.NotNull(compiler.Compile(template.Draft, "owner", ResourceScope.Session("owner", 1)));
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "标准方案条件", "synthetic", Detection(), template.Draft.Action);
        Assert.True(UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!.Paused);
        Assert.Contains(catalog.Combinations, c => c.DetectionId == PowerSchemeStateCapabilities.State && c.ActionId == SystemSoundCapabilities.Sound);
        var sound = new TaskInvocation(SystemSoundCapabilities.Sound, 1,
            ImmutableDictionary<string, string>.Empty.Add("sound", "info").Add("delaySeconds", "0"));
        var soundDraft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = sound }), recorder.Session, catalog).Draft!;
        Assert.True(soundDraft.Paused); Assert.NotNull(compiler.Compile(soundDraft, "owner", ResourceScope.Session("owner", 1)));
        foreach (var value in new[] { "custom", "Balanced", "", PowerSchemeCapabilities.Saver.ToString() })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection(value) }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => PowerSchemeStateCapabilities.Validate(Detection() with { SchemaVersion = 2 }));
        Assert.Throws<ArgumentException>(() => PowerSchemeStateCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.Add("extra", "1") }));
        var path = Path.Combine(Path.GetTempPath(), "axiom-synthetic-scheme-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(template.Draft, default);
            using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync());
            Assert.Equal(template.Draft.Detection.Parameters, loaded.Detection.Parameters); Assert.NotNull(compiler.Compile(loaded, "owner", ResourceScope.Session("owner", 1)));
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
        foreach (var (id, guid) in new[] { ("balanced", PowerSchemeCapabilities.Balanced), ("saver", PowerSchemeCapabilities.Saver), ("performance", PowerSchemeCapabilities.Performance) })
        {
            Assert.True(PowerSchemeStateState.Evaluate(id, guid)); Assert.Null(PowerSchemeStateState.Evaluate(id, Guid.Empty)); Assert.Null(PowerSchemeStateState.Evaluate(id, null));
            Assert.False(PowerSchemeStateState.Evaluate(id, new Guid("10000000-0000-0000-0000-000000000001"))); // valid custom scheme is Known false
        }
    }

    [Fact(Timeout = 10000)]
    public async Task BoundedHintsReadOnceAndQueryUnknownResetOrResumeRecoverOnlyAsBaseline()
    {
        var hints = Channel.CreateBounded<string>(64); var updates = Channel.CreateUnbounded<(PowerContextValue[] Values, bool Baseline)>();
        using var stop = new CancellationTokenSource(); var current = PowerSchemeCapabilities.Saver; var reads = 0; var queryFails = false;
        for (var i = 0; i < 64; i++) hints.Writer.TryWrite("power-scheme-changed");
        var run = PowerSchemeStateObservation.RunAsync(["saver", "balanced"], hints.Reader, () => { reads++; if (queryFails) throw new IOException(); return current; },
            (v, b) => updates.Writer.TryWrite((v, b)), stop.Token);
        Assert.True((await updates.Reader.ReadAsync()).Baseline); Assert.Equal(2, reads); Assert.False(updates.Reader.TryRead(out _));
        current = PowerSchemeCapabilities.Balanced; hints.Writer.TryWrite("power-scheme-changed"); var change = await updates.Reader.ReadAsync(); Assert.False(change.Baseline); Assert.False(change.Values[0].Satisfied);
        queryFails = true; hints.Writer.TryWrite("power-scheme-changed"); Assert.All((await updates.Reader.ReadAsync()).Values, v => Assert.Null(v.Satisfied));
        queryFails = false; hints.Writer.TryWrite("power-scheme-changed"); Assert.True((await updates.Reader.ReadAsync()).Baseline);
        hints.Writer.TryWrite("power-scheme-reset"); Assert.All((await updates.Reader.ReadAsync()).Values, v => Assert.Null(v.Satisfied));
        hints.Writer.TryWrite("resumed"); Assert.True((await updates.Reader.ReadAsync()).Baseline);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact(Timeout = 10000)]
    public async Task HintFailurePreventsStaleKnownPublishAndProviderRemainsUnknownUntilCanceled()
    {
        var hints = Channel.CreateBounded<string>(64); var publications = 0; var reads = 0; var failed = false;
        var run = PowerSchemeStateObservation.RunAsync(["saver"], hints.Reader, () => { if (++reads == 2) failed = true; return PowerSchemeCapabilities.Saver; },
            (_, _) => publications++, default, () => failed);
        hints.Writer.TryWrite("power-scheme-changed"); await Assert.ThrowsAsync<IOException>(() => run); Assert.Equal(1, publications);
        foreach (var initial in new[] { false, true })
        {
            using var stop = new CancellationTokenSource(); var updates = Channel.CreateUnbounded<ProviderUpdate>();
            var provider = new PowerSchemeStateCapabilityProvider((ids, publish, _) => { if (initial) publish([new(ids[0], true)], true); throw new IOException(); });
            var observe = provider.ObserveAsync([new(PowerSchemeStateCapabilities.State, "saver")], u => updates.Writer.TryWrite(u), stop.Token);
            if (initial) Assert.True((await updates.Reader.ReadAsync()).Snapshot);
            var unknown = await updates.Reader.ReadAsync(); Assert.Equal(!initial, unknown.Snapshot); Assert.Equal(StateQuality.Unknown, Assert.Single(unknown.Samples).Quality); Assert.False(observe.IsCompleted);
            stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observe);
            await Assert.ThrowsAsync<ArgumentException>(() => provider.ObserveAsync([new(PowerSchemeStateCapabilities.State, "custom")], _ => { }, default));
        }
    }

    [Fact(Timeout = 10000)]
    public async Task InitialRecoveryAndPausedConditionsDoNotReplayCommandsWhileDesiredReleases()
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(PowerSchemeStateCapabilities.State, schema: schema));
        var condition = new PowerSchemeStateTaskDetection().Compile(Detection(), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-scheme", [key]); long sequence = 0;
        void Set(Guid? guid, bool baseline = false)
        {
            var value = PowerSchemeStateState.Evaluate("saver", guid); var observation = new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        var rule = new RuleDefinition("synthetic-scheme-rule", 1, "owner", ResourceScope.Machine, condition, RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Set(PowerSchemeCapabilities.Saver, true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(PowerSchemeCapabilities.Balanced); Set(PowerSchemeCapabilities.Saver); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(null); Set(PowerSchemeCapabilities.Saver, true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", rule.Scope, condition, rule.Action, enabled: false)); Set(PowerSchemeCapabilities.Balanced); Set(PowerSchemeCapabilities.Saver);
        fixture.Engine.SetRule(new(rule.Id, 3, "owner", rule.Scope, condition, rule.Action)); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.Stop();
        var desired = new DesiredExecutor(); using var df = new AutomationFixture(desired);
        df.Engine.RegisterCapability(AutomationFixture.Binding(PowerSchemeStateCapabilities.State, schema: schema)); var ds = df.Engine.RegisterSource("synthetic-desired-scheme", [key]); long seq = 0;
        void D(bool? value) => df.Engine.ApplySnapshot(ds, ++seq, [new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown, value.HasValue ? StateValue.From(schema, value.Value) : null, df.Clock.UtcNow)]);
        var dr = new RuleDefinition("synthetic-desired", 1, "owner", ResourceScope.Machine, condition, RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        D(true); df.Engine.SetRule(dr); df.Engine.StartExecution(); await df.Engine.WaitForIdleAsync(); Assert.True(desired.Authorized);
        D(false); Assert.False(desired.Authorized); D(true); await df.Engine.WaitForIdleAsync(); D(null); Assert.False(desired.Authorized);
        D(true); await df.Engine.WaitForIdleAsync(); df.Engine.SetRule(new(dr.Id, 2, "owner", dr.Scope, condition, dr.Action, enabled: false)); Assert.False(desired.Authorized);
        df.Engine.SetRule(new(dr.Id, 3, "owner", dr.Scope, condition, dr.Action)); await df.Engine.WaitForIdleAsync(); df.Engine.Stop(); Assert.False(desired.Authorized);
    }
    private sealed class DesiredExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public bool Authorized;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) => Authorized = authorized;
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException();
    }
}
