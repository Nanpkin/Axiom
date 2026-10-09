using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class PowerSaverStateTests
{
    private static TaskInvocation Detection(string state = "on") => new(PowerSaverStateCapabilities.State, 1,
        ImmutableDictionary<string, string>.Empty.Add("state", state));

    [Fact]
    public async Task RegistryPausedTemplateSaveAndStrictImportNeverInferStateOrAllowPowerFeedback()
    {
        var modules = BuiltInAutomationPlugins.Create(); var plugin = Assert.Single(modules.OfType<PowerSaverStatePlugin>());
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        Assert.DoesNotContain(plugin.Combinations, c => c.ActionId is PowerSchemeCapabilities.Set or KeepAwakeCapabilities.Hold);
        Assert.Contains(plugin.Combinations, c => c.ActionId == BuiltInBrightnessCapabilities.Set);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "windows-saver-dim-panel"); Assert.True(template.Draft.Paused);
        var compiler = new AutomationTaskCompiler();
        foreach (var m in modules) { foreach (var d in m.Detections) compiler.Register(d); foreach (var a in m.Actions) compiler.Register(a); }
        Assert.NotNull(compiler.Compile(template.Draft, "owner", ResourceScope.Session("owner", 1)));
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "Windows节能报告", "synthetic", Detection(), template.Draft.Action);
        Assert.True(UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!.Paused);
        foreach (var value in new[] { "battery-below", "saver", "On", "", "2", "255" })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection(value) }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => PowerSaverStateCapabilities.Validate(Detection() with { SchemaVersion = 2 }));
        foreach (var extra in new[] { "threshold", "battery", "scheme" })
            Assert.Throws<ArgumentException>(() => PowerSaverStateCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.Add(extra, "1") }));
        var path = Path.Combine(Path.GetTempPath(), "axiom-synthetic-saver-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(template.Draft, default);
            using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync());
            Assert.Equal(template.Draft.Detection.Parameters, loaded.Detection.Parameters); Assert.NotNull(compiler.Compile(loaded, "owner", ResourceScope.Session("owner", 1)));
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
        Assert.True(PowerSaverStateState.Evaluate("on", 1)); Assert.False(PowerSaverStateState.Evaluate("off", 1));
        Assert.True(PowerSaverStateState.Evaluate("off", 0)); Assert.False(PowerSaverStateState.Evaluate("on", 0));
        foreach (byte? unknown in new byte?[] { null, 2, 128, 255 })
        { Assert.Null(PowerSaverStateState.Evaluate("on", unknown)); Assert.Null(PowerSaverStateState.Evaluate("off", unknown)); }
    }

    [Fact(Timeout = 10000)]
    public async Task BoundedHintsReadOnceAndUnknownResetOrResumeRecoverOnlyAsBaseline()
    {
        var hints = Channel.CreateBounded<string>(64); var updates = Channel.CreateUnbounded<(PowerContextValue[] Values, bool Baseline)>();
        using var stop = new CancellationTokenSource(); byte current = 1; var reads = 0; var queryFails = false;
        for (var i = 0; i < 64; i++) hints.Writer.TryWrite("power-saver-changed");
        var run = PowerSaverStateObservation.RunAsync(["on", "off"], hints.Reader, () => { reads++; if (queryFails) throw new IOException(); return current; },
            (v, b) => updates.Writer.TryWrite((v, b)), stop.Token);
        Assert.True((await updates.Reader.ReadAsync()).Baseline); Assert.Equal(2, reads); Assert.False(updates.Reader.TryRead(out _));
        current = 0; hints.Writer.TryWrite("power-saver-changed"); var change = await updates.Reader.ReadAsync(); Assert.False(change.Baseline); Assert.False(change.Values[0].Satisfied); Assert.True(change.Values[1].Satisfied);
        queryFails = true; hints.Writer.TryWrite("power-saver-changed"); Assert.All((await updates.Reader.ReadAsync()).Values, v => Assert.Null(v.Satisfied));
        queryFails = false; current = 1; hints.Writer.TryWrite("power-saver-changed"); Assert.True((await updates.Reader.ReadAsync()).Baseline);
        current = 2; hints.Writer.TryWrite("power-saver-changed"); Assert.All((await updates.Reader.ReadAsync()).Values, v => Assert.Null(v.Satisfied));
        current = 0; hints.Writer.TryWrite("power-saver-changed"); Assert.True((await updates.Reader.ReadAsync()).Baseline);
        hints.Writer.TryWrite("power-saver-reset"); hints.Writer.TryWrite("resumed");
        Assert.All((await updates.Reader.ReadAsync()).Values, v => Assert.Null(v.Satisfied)); Assert.True((await updates.Reader.ReadAsync()).Baseline);
        hints.Writer.TryWrite("resumed"); Assert.True((await updates.Reader.ReadAsync()).Baseline);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact(Timeout = 10000)]
    public async Task HintFailurePreventsStaleKnownPublishAndProviderRemainsUnknownUntilCanceled()
    {
        var hints = Channel.CreateBounded<string>(64); var publications = 0; var reads = 0; var failed = false;
        var run = PowerSaverStateObservation.RunAsync(["on"], hints.Reader, () => { if (++reads == 2) failed = true; return (byte)1; },
            (_, _) => publications++, default, () => failed);
        hints.Writer.TryWrite("power-saver-changed"); await Assert.ThrowsAsync<IOException>(() => run); Assert.Equal(1, publications);
        foreach (var initial in new[] { false, true })
        {
            using var stop = new CancellationTokenSource(); var updates = Channel.CreateUnbounded<ProviderUpdate>();
            var provider = new PowerSaverStateCapabilityProvider((ids, publish, _) => { if (initial) publish([new(ids[0], true)], true); throw new IOException(); });
            var observe = provider.ObserveAsync([new(PowerSaverStateCapabilities.State, "on")], u => updates.Writer.TryWrite(u), stop.Token);
            if (initial) Assert.True((await updates.Reader.ReadAsync()).Snapshot);
            var unknown = await updates.Reader.ReadAsync(); Assert.Equal(!initial, unknown.Snapshot); Assert.Equal(StateQuality.Unknown, Assert.Single(unknown.Samples).Quality); Assert.False(observe.IsCompleted);
            stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observe);
            await Assert.ThrowsAsync<ArgumentException>(() => provider.ObserveAsync([new(PowerSaverStateCapabilities.State, "2")], _ => { }, default));
            await Assert.ThrowsAsync<ArgumentException>(() => provider.ObserveAsync([new(PowerSaverStateCapabilities.State, "on"), new(PowerSaverStateCapabilities.State, "on")], _ => { }, default));
        }
    }

    [Fact(Timeout = 10000)]
    public async Task InitialRecoveryResumeAndPausedConditionsDoNotReplayCommands()
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(PowerSaverStateCapabilities.State, schema: schema));
        var condition = new PowerSaverStateTaskDetection().Compile(Detection(), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-saver", [key]); long sequence = 0;
        void Set(byte? status, bool baseline = false)
        {
            var value = PowerSaverStateState.Evaluate("on", status); var observation = new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        var rule = new RuleDefinition("synthetic-saver-rule", 1, "owner", ResourceScope.Machine, condition, RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Set(1, true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(0); Set(1); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(255); Set(1, true); Set(0, true); Set(1, true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", rule.Scope, condition, rule.Action, enabled: false)); Set(0); Set(1);
        fixture.Engine.SetRule(new(rule.Id, 3, "owner", rule.Scope, condition, rule.Action)); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(0); Set(1); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
        fixture.Engine.Stop(); Assert.Throws<InvalidOperationException>(() => Set(0)); Assert.Equal(2, executor.Count);
    }
}
