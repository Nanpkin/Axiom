using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class ForegroundScreenCoverTests
{
    private static TaskInvocation Detection() => new(ForegroundScreenCoverCapabilities.Covers, 1, ImmutableDictionary<string, string>.Empty);
    private static readonly ForegroundCoverIdentity A = new(11, 101, 201, 301);
    private static readonly ForegroundCoverIdentity B = new(12, 102, 202, 302);

    [Fact]
    public void PhysicalFrameMustCoverMonitorBoundsRatherThanWorkAreaAcrossNegativeAndScaledCoordinates()
    {
        var screen = new MonitorRectangle(0, 0, 1920, 1080);
        Assert.False(ForegroundScreenCoverGeometry.Covers(new(0, 0, 1920, 1040), screen)); // maximized work area
        Assert.True(ForegroundScreenCoverGeometry.Covers(screen, screen)); // auto-hide taskbar may have this work area
        Assert.True(ForegroundScreenCoverGeometry.Covers(new(-8, -8, 1928, 1088), screen));
        Assert.False(ForegroundScreenCoverGeometry.Covers(new(1, 0, 1920, 1080), screen)); // no invented pixel tolerance
        var negative = new MonitorRectangle(-2560, -1440, 0, 0);
        Assert.True(ForegroundScreenCoverGeometry.Covers(negative, negative));
        Assert.False(ForegroundScreenCoverGeometry.Covers(new(-1707, -960, 0, 0), negative)); // virtualized 150% coordinates cannot match physical bounds
        Assert.Throws<ArgumentException>(() => ForegroundScreenCoverGeometry.Covers(new(0, 0, 0, 1), screen));
    }

    [Fact(Timeout = 10000)]
    public async Task SelectionChangesUnknownAndCoalescedDestructionOnlyRecoverAsBaselines()
    {
        using var stop = new CancellationTokenSource(); using var source = new FakeSource(); var updates = Channel.CreateUnbounded<ForegroundCoverSample>();
        var run = ForegroundScreenCoverObservation.RunAsync(source, s => updates.Writer.TryWrite(s), stop.Token);
        var initial = await updates.Reader.ReadAsync(); Assert.True(initial.Baseline); Assert.False(initial.Covers); // no app is definite false
        source.Reading = new(A, true); source.Emit(); Assert.True((await updates.Reader.ReadAsync()).Baseline);
        source.Reading = new(A, false); source.Emit(); Assert.False((await updates.Reader.ReadAsync()).Baseline);
        source.Reading = new(A, true); source.Emit(); var entering = await updates.Reader.ReadAsync(); Assert.False(entering.Baseline); Assert.True(entering.Covers);
        source.Reading = new(B, true); source.Emit(); Assert.True((await updates.Reader.ReadAsync()).Baseline);
        source.Reading = new(B, null); source.Emit(); Assert.Null((await updates.Reader.ReadAsync()).Covers);
        source.Reading = new(B, true); source.Emit(); Assert.True((await updates.Reader.ReadAsync()).Baseline);
        // Prequeued reset plus known must publish Unknown then a fresh baseline, even with unchanged identity.
        var batch = new FakeSource { Reading = new(A, true) }; var samples = new List<ForegroundCoverSample>(); var first = true;
        var batched = ForegroundScreenCoverObservation.RunAsync(batch, s =>
        {
            samples.Add(s); if (!first) return; first = false;
            batch.Emit(reset: true); batch.Emit(); batch.Queue.Writer.TryComplete();
        }, default);
        await Assert.ThrowsAsync<IOException>(() => batched);
        Assert.Collection(samples, s => Assert.True(s.Baseline), s => Assert.Null(s.Covers), s => { Assert.True(s.Covers); Assert.True(s.Baseline); });
        batch.Dispose(); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact(Timeout = 10000)]
    public async Task FailedSourceDropsCachedKnownBeforeReadOrDisposeAndNeverReconnectsAutomatically()
    {
        using var stop = new CancellationTokenSource(); using var source = new FakeSource { Reading = new(A, true), Failed = true };
        for (var i = 0; i < 64; i++) source.Emit();
        var updates = Channel.CreateUnbounded<ProviderUpdate>(); var starts = 0;
        source.Disposing = () => Assert.True(updates.Reader.TryPeek(out var update) && update.Samples[0].Quality == StateQuality.Unknown);
        var provider = new ForegroundScreenCoverCapabilityProvider(() => { starts++; return source; });
        var run = provider.ObserveAsync([new(ForegroundScreenCoverCapabilities.Covers, ForegroundScreenCoverCapabilities.Resource)], s => updates.Writer.TryWrite(s), stop.Token);
        Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync()).Samples[0].Quality);
        Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync()).Samples[0].Quality);
        Assert.Equal(0, source.Reads); Assert.Equal(1, starts); Assert.False(run.IsCompleted); Assert.False(updates.Reader.TryRead(out _));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var canceled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ObserveAsync([new(ForegroundScreenCoverCapabilities.Covers, "current")], _ => { }, canceled));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ObserveAsync([new(ForegroundScreenCoverCapabilities.Covers, "exe")], _ => { }, default)); Assert.Equal(1, starts);
        // Query-time loss also prevents publishing the just-read Known sample.
        using var duringRead = new FakeSource { Reading = new(A, true) }; duringRead.ReadingAction = () => duringRead.Failed = true;
        var count = 0; await Assert.ThrowsAsync<IOException>(() => ForegroundScreenCoverObservation.RunAsync(duringRead, _ => count++, default)); Assert.Equal(0, count);
    }

    [Fact]
    public async Task RegistryNoParametersPausedTemplateStrictImportAndStoredDraftAreUsable()
    {
        var modules = BuiltInAutomationPlugins.Create(); var plugin = Assert.Single(modules.OfType<ForegroundScreenCoverPlugin>());
        Assert.Contains(plugin.Combinations, c => c.ActionId == KeepAwakeCapabilities.Hold);
        Assert.DoesNotContain(plugin.Combinations, c => c.ActionId is WindowCapabilities.State or WindowMonitorCapabilities.Move or HotkeyCapabilities.Send or "program.start" or "program.close");
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var draft = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "foreground-cover-keep-awake").Draft; Assert.True(draft.Paused); Assert.Empty(draft.Detection.Parameters);
        var compiler = new AutomationTaskCompiler(); foreach (var m in modules) { foreach (var d in m.Detections) compiler.Register(d); foreach (var a in m.Actions) compiler.Register(a); }
        Assert.NotNull(compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "覆盖屏幕", "synthetic", Detection(), draft.Action);
        Assert.True(UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!.Paused);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection() with { Parameters = Detection().Parameters.Add("fullscreen", "true") } }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => ForegroundScreenCoverCapabilities.Validate(Detection() with { SchemaVersion = 2 }));
        var path = Path.Combine(Path.GetTempPath(), "axiom-synthetic-cover-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(draft, default);
            using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync());
            Assert.Equal(draft.Detection.Parameters, loaded.Detection.Parameters); Assert.NotNull(compiler.Compile(loaded, "owner", ResourceScope.Session("owner", 1)));
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact(Timeout = 10000)]
    public async Task KnownInitialDesiredHoldsAndFalseUnknownPauseReleaseWhileCommandsRespectBaselines()
    {
        var executor = new CoverDesiredExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(ForegroundScreenCoverCapabilities.Covers, schema: schema));
        var condition = new ForegroundScreenCoverTaskDetection().Compile(Detection(), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-cover", [key]); long seq = 0;
        void Set(bool? value) => fixture.Engine.ApplySnapshot(source, ++seq, [new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
            value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow)]);
        var rule = new RuleDefinition("cover-desired", 1, "owner", ResourceScope.Machine, condition, RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Set(true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        Set(false); Assert.False(executor.Authorized); Set(true); await fixture.Engine.WaitForIdleAsync(); Set(null); Assert.False(executor.Authorized);
        Set(true); await fixture.Engine.WaitForIdleAsync(); fixture.Engine.SetRule(new(rule.Id, 2, "owner", rule.Scope, condition, rule.Action, enabled: false)); Assert.False(executor.Authorized);
        var command = new ControlledExecutor(); using var cf = new AutomationFixture(command);
        cf.Engine.RegisterCapability(AutomationFixture.Binding(ForegroundScreenCoverCapabilities.Covers, schema: schema)); var cs = cf.Engine.RegisterSource("synthetic-cover-command", [key]); long sequence = 0;
        void C(bool value, bool baseline) { var o = new Observation(key, StateQuality.Known, StateValue.From(schema, value), cf.Clock.UtcNow); if (baseline) cf.Engine.ApplySnapshot(cs, ++sequence, [o]); else cf.Engine.Update(cs, ++sequence, o); }
        C(true, true); cf.Engine.SetRule(new("cover-command", 1, "owner", ResourceScope.Machine, condition, RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true))));
        cf.Engine.StartExecution(); await cf.Engine.WaitForIdleAsync(); Assert.Equal(0, command.Count);
        C(false, false); C(true, true); await cf.Engine.WaitForIdleAsync(); Assert.Equal(0, command.Count); // foreground selection baseline
        C(false, false); C(true, false); (await command.NextAsync()).Complete(); await cf.Engine.WaitForIdleAsync(); Assert.Equal(1, command.Count);
    }
    private sealed class FakeSource : IForegroundScreenCoverSource
    {
        public readonly Channel<ForegroundCoverHint> Queue = Channel.CreateBounded<ForegroundCoverHint>(64);
        public ChannelReader<ForegroundCoverHint> Events => Queue.Reader;
        public bool Failed { get; set; }
        public ForegroundCoverReading Reading = new(null, false);
        public int Reads; public Action? ReadingAction, Disposing;
        public ForegroundCoverReading Read() { Reads++; ReadingAction?.Invoke(); return Reading; }
        public void Emit(bool reset = false) => Assert.True(Queue.Writer.TryWrite(new(reset)));
        public void Dispose() { Disposing?.Invoke(); Disposing = null; Queue.Writer.TryComplete(); }
    }
    private sealed class CoverDesiredExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public bool Authorized;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) => Authorized = authorized;
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic lease", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException("Unexpected command.");
    }
}
