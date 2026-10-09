using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class SessionPresenceTests
{
    private static TaskInvocation Detection(string state = "absent") => new(SessionPresenceCapabilities.State, 1,
        ImmutableDictionary<string, string>.Empty.Add("presence", state));

    [Fact]
    public async Task ThreeRawStatesStrictImportSaveAndPausedTemplateRejectFeedback()
    {
        foreach (var (id, raw, kind) in new[] { ("present", 0u, "presence-present"), ("absent", 1u, "presence-absent"), ("inactive", 2u, "presence-inactive") })
        {
            Assert.Equal(raw, SessionPresenceState.Decode(kind)); Assert.True(SessionPresenceState.Evaluate(id, raw));
            Assert.False(SessionPresenceState.Evaluate(id, (raw + 1) % 3)); Assert.Null(SessionPresenceState.Evaluate(id, 3)); Assert.Null(SessionPresenceState.Evaluate(id, null));
        }
        Assert.Null(SessionPresenceState.Decode("presence-reset"));
        var modules = BuiltInAutomationPlugins.Create(); var plugin = Assert.Single(modules.OfType<SessionPresencePlugin>());
        Assert.Equal(2, plugin.Combinations.Length);
        Assert.DoesNotContain(plugin.Combinations, c => c.ActionId == CurrentMediaCapabilities.Control);
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var draft = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "session-absent-mute").Draft; Assert.True(draft.Paused);
        var compiler = new AutomationTaskCompiler(); foreach (var m in modules) { foreach (var d in m.Detections) compiler.Register(d); foreach (var a in m.Actions) compiler.Register(a); }
        Assert.NotNull(compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "在场检测", "synthetic", Detection(), draft.Action);
        Assert.True(UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!.Paused);
        Assert.Equal(new[] { AudioPlaybackCapabilities.Set, NotificationCapabilities.Show, SystemSoundCapabilities.Sound }.Order(),
            catalog.Combinations.Where(c => c.DetectionId == SessionPresenceCapabilities.State).Select(c => c.ActionId).Order());
        var sound = new TaskInvocation(SystemSoundCapabilities.Sound, 1,
            ImmutableDictionary<string, string>.Empty.Add("sound", "info").Add("delaySeconds", "0"));
        var soundDraft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = sound }), recorder.Session, catalog).Draft!;
        Assert.True(soundDraft.Paused); Assert.NotNull(compiler.Compile(soundDraft, "owner", ResourceScope.Session("owner", 1)));
        foreach (var invalid in new[] { "idle", "Absent", "", "1" })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection(invalid) }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => SessionPresenceCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.Add("minutes", "5") }));
        foreach (var operation in new[] { "unmute", "volume" })
        {
            var action = draft.Action with { Parameters = draft.Action.Parameters.SetItem("operation", operation) };
            Assert.NotNull(compiler.Compile(draft with { Action = action }, "owner", ResourceScope.Session("owner", 1)));
            Assert.NotNull(UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = action }), recorder.Session, catalog).Draft);
        }
        var pause = new TaskInvocation(CurrentMediaCapabilities.Control, 1, ImmutableDictionary<string, string>.Empty.Add("operation", "pause").Add("delaySeconds", "0"));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = pause }), recorder.Session, catalog));
        var path = Path.Combine(Path.GetTempPath(), "axiom-synthetic-presence-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(draft, default);
            using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync());
            Assert.Equal(draft.Detection.Parameters, loaded.Detection.Parameters); Assert.NotNull(compiler.Compile(loaded, "owner", ResourceScope.Session("owner", 1)));
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact(Timeout = 10000)]
    public async Task UnknownAndCoalescedRecoveryPreserveCommandBaseline()
    {
        using var stop = new CancellationTokenSource(); var source = new FakeSource(); var updates = Channel.CreateUnbounded<ProviderUpdate>();
        var run = new SessionPresenceCapabilityProvider(() => source).ObserveAsync([new(SessionPresenceCapabilities.State, "absent")], u => updates.Writer.TryWrite(u), stop.Token);
        Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync()).Samples[0].Quality);
        source.Queue.Writer.TryWrite("presence-absent"); var first = await updates.Reader.ReadAsync(); Assert.True(first.Snapshot); Assert.True(first.Samples[0].Boolean);
        // Pause the synchronous publisher so reset and Known are guaranteed to coalesce in one bounded batch.
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var hints = Channel.CreateBounded<string>(64); var samples = Channel.CreateUnbounded<SessionPresenceSample>();
        hints.Writer.TryWrite("presence-present");
        var observation = Task.Factory.StartNew(() => SessionPresenceObservation.RunAsync(hints.Reader,
            sample => { samples.Writer.TryWrite(sample); if (sample.State == 0) { entered.TrySetResult(true); release.Wait(); } }, stop.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        try
        {
            try
            {
                Assert.True(await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
                hints.Writer.TryWrite("presence-reset"); hints.Writer.TryWrite("presence-absent");
            }
            finally { release.Set(); }
            Assert.True((await samples.Reader.ReadAsync()).Baseline); Assert.Null((await samples.Reader.ReadAsync()).State);
            var recovery = await samples.Reader.ReadAsync(); Assert.True(recovery.Baseline); Assert.Equal(1u, recovery.State);
        }
        finally
        {
            stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task SourceLossDiscards64CachedReportsAndRegistrationFailureStaysUnknown()
    {
        foreach (var registrationFails in new[] { false, true })
        {
            var source = new FakeSource(); for (var i = 0; i < 64; i++) source.Queue.Writer.TryWrite("presence-absent"); source.Failed = true;
            source.Queue.Writer.TryComplete(new IOException("synthetic loss"));
            var starts = 0; using var stop = new CancellationTokenSource(); var updates = Channel.CreateUnbounded<ProviderUpdate>();
            var provider = new SessionPresenceCapabilityProvider(() => { starts++; if (registrationFails) throw new IOException("registration unavailable"); return source; });
            var run = provider.ObserveAsync([new(SessionPresenceCapabilities.State, "absent")], u => updates.Writer.TryWrite(u), stop.Token);
            Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync()).Samples[0].Quality);
            Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync()).Samples[0].Quality);
            Assert.False(updates.Reader.TryRead(out _)); Assert.False(run.IsCompleted); Assert.Equal(1, starts);
            stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task FirstRecoveryAndReenableNeverReplayButNewAbsentEdgeExecutes()
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(SessionPresenceCapabilities.State, schema: schema));
        var condition = new SessionPresenceTaskDetection().Compile(Detection(), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-presence", [key]); long sequence = 0;
        void Set(bool? value, bool baseline = false)
        {
            var observation = new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown, value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        var rule = new RuleDefinition("synthetic-presence-rule", 1, "owner", ResourceScope.Machine, condition, RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Set(true, true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(false); Set(true); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(null); Set(true, true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", rule.Scope, condition, rule.Action, enabled: false)); Set(false); Set(true);
        fixture.Engine.SetRule(new(rule.Id, 3, "owner", rule.Scope, condition, rule.Action)); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        var canceled = new CancellationToken(true); var starts = 0; var provider = new SessionPresenceCapabilityProvider(() => { starts++; return new FakeSource(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ObserveAsync([new(SessionPresenceCapabilities.State, "absent")], _ => { }, canceled));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ObserveAsync([new(SessionPresenceCapabilities.State, "idle")], _ => { }, default)); Assert.Equal(0, starts);
    }
    private sealed class FakeSource : ISessionPresenceSource
    {
        public readonly Channel<string> Queue = Channel.CreateBounded<string>(64);
        public ChannelReader<string> Events => Queue.Reader;
        public bool Failed { get; set; }
        public void Dispose() => Queue.Writer.TryComplete();
    }
}
