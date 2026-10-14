using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class ApplicationAudioActivityTests
{
    private static string App(string name = "SyntheticPlayer") => Path.Combine(Path.GetTempPath(), name + ".exe");
    private static TaskInvocation Detection(string? paths = null) => new(ApplicationAudioActivityCapabilities.Active, 1,
        ImmutableDictionary<string, string>.Empty.Add("executable", paths ?? App()));
    private static async Task<ApplicationAudioActivitySample> Read(IApplicationAudioActivitySource source, bool? value)
    {
        while (true) { var sample = await source.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); if (sample.Active == value) return sample; }
    }
    [Fact]
    public void ThreeValuedAggregationPinsSelectedIdentityAndExitedOldActiveNeverFollowsReusedPid()
    {
        var path = App(); var node = new Session { Path = path };
        var active = ApplicationAudioSessionReading.Read(node, [path], null);
        Assert.True(active.Active); Assert.Equal(1, node.Watches);
        node.Exited = true; node.Identity = AudioSessionIdentity.Unknown;
        var exited = ApplicationAudioSessionReading.Read(node, [path], path);
        Assert.False(exited.Active); Assert.Equal(1, node.Identifications); // No lookup of a replacement PID.
        node.Exited = false; node.AudioState = 0;
        Assert.False(ApplicationAudioSessionReading.Read(node, [path], path).Active);
        node.AudioState = 2; Assert.False(ApplicationAudioSessionReading.Read(node, [path], path).Active);
        node.AudioState = 1;
        var unknown = ApplicationAudioSessionReading.Read(node, [path], null); Assert.Null(unknown.Active);
        Assert.Null(ApplicationAudioProjection.Match([unknown], path));
        Assert.True(ApplicationAudioProjection.Match([unknown, active], path));
        Assert.False(ApplicationAudioProjection.Match([], path));
        node.Identity = AudioSessionIdentity.Other; Assert.False(ApplicationAudioSessionReading.Read(node, [path], null).Active);
        node.Identity = AudioSessionIdentity.Owned; node.Path = App("Unselected");
        Assert.False(ApplicationAudioSessionReading.Read(node, [path], null).Active); Assert.Equal(1, node.Watches);
    }
    [Fact(Timeout = 10000)]
    public async Task SharedSourceReleasesLastLeaseAndUnknownRecoveryIsBaselineWhileNoSessionsIsFalse()
    {
        var api = new Api { Values = [new(App(), true)] }; var opens = 0;
        var hub = new ApplicationAudioActivityHub(notify => { api.Notify = notify; Interlocked.Increment(ref opens); return api; });
        using var first = hub.Subscribe([App()]); Assert.True((await Read(first, true)).Baseline);
        using var second = hub.Subscribe([App()]); Assert.True((await Read(second, true)).Baseline);
        Assert.Equal(1, opens); first.Dispose(); Assert.False(hub.Stopped.Task.IsCompleted);
        api.Values = [new(null, null)]; api.Emit(); await Read(second, null);
        api.Values = [new(App(), true)]; api.Emit(); Assert.True((await Read(second, true)).Baseline);
        api.Values = []; api.Emit(); Assert.False((await Read(second, false)).Baseline);
        second.Dispose(); await hub.Stopped.Task; Assert.Equal(1, api.Disposals); Assert.Equal(api.ReadThread, api.DisposeThread);
    }
    [Fact(Timeout = 10000)]
    public async Task DefaultChangeImmediatelyDropsKnownAndRejectsBlockedOldResultAndLateCallback()
    {
        var api = new Api { Values = [new(App(), false)] };
        var hub = new ApplicationAudioActivityHub(notify => { api.Notify = notify; return api; }); using var source = hub.Subscribe([App()]); await Read(source, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        api.Values = [new(App(), true)]; api.BeforeRead = () => { entered.TrySetResult(); release.Wait(); };
        try
        {
            // The observer channel's wakeup may need the pool; do not occupy it while awaiting the native-read barrier.
            api.Emit(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            api.Notify!(new("default")); Assert.Null((await Read(source, null)).Active);
            var pointer = new Payload(); api.Notify(new("created", 0, pointer)); Assert.True(pointer.Disposed);
            api.Values = [new(App(), false)]; api.BeforeRead = null; release.Set();
            Assert.True((await Read(source, false)).Baseline); Assert.False(source.Events.TryRead(out _));
        }
        finally { release.Set(); }
    }
    [Fact(Timeout = 10000)]
    public async Task NotificationOverflowMakesProviderUnknownBeforeBlockedNativeCleanupAndRefusesReplacement()
    {
        var api = new Api { Values = [new(App(), true)] };
        var hub = new ApplicationAudioActivityHub(notify => { api.Notify = notify; return api; });
        var updates = Channel.CreateBounded<ProviderUpdate>(16); using var stop = new CancellationTokenSource();
        var run = new ApplicationAudioActivityCapabilityProvider(hub.Subscribe).ObserveAsync([new(ApplicationAudioActivityCapabilities.Active, App())], u => Assert.True(updates.Writer.TryWrite(u)), stop.Token);
        while ((await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Samples[0].Quality != StateQuality.Known) { }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        api.BeforeRead = () => { entered.TrySetResult(); release.Wait(); };
        try
        {
            api.Emit(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); for (var i = 0; i < 65; i++) api.Emit();
            Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Samples[0].Quality);
            Assert.False(updates.Reader.TryRead(out _)); Assert.False(hub.Stopped.Task.IsCompleted);
            Assert.Throws<IOException>(() => hub.Subscribe([App()]));
        }
        finally { release.Set(); stop.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); await hub.Stopped.Task; Assert.Equal(1, api.Disposals);
    }
    [Fact(Timeout = 10000)]
    public async Task InitialActiveHoldsDesiredUnknownReleasesAndRecoveredActiveDoesNotReplayCommand()
    {
        var executor = new Executor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(ApplicationAudioActivityCapabilities.Active, schema: schema));
        var condition = new ApplicationAudioActivityTaskDetection().Compile(Detection(), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-application-audio", [key]); long sequence = 0;
        void Set(bool? active, bool baseline)
        {
            var observation = new Observation(key, active.HasValue ? StateQuality.Known : StateQuality.Unknown, active.HasValue ? StateValue.From(schema, active.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        Set(true, true);
        fixture.Engine.SetRule(new("awake", 1, "owner", ResourceScope.Machine, condition, RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true))));
        fixture.Engine.SetRule(new("notice", 1, "owner", ResourceScope.Machine, condition, RuleAction.Command(new("output", "notice", ResourceScope.Machine), StateValue.From(AutomationFixture.Schema, true))));
        fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized); Assert.Equal(0, executor.Commands);
        fixture.Update(true, AutomationFixture.Output); Set(null, false); Assert.False(executor.Authorized);
        Set(true, true); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized); Assert.Equal(0, executor.Commands);
        Set(false, false); Set(true, false); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Commands);
        fixture.Engine.RemoveRule("awake"); Assert.False(executor.Authorized);
    }
    [Fact]
    public async Task FullPathMultiselectStrictAliasImportAndPausedTemplateSaveWithoutNativeEffects()
    {
        var plugins = BuiltInAutomationPlugins.Create(); var compiler = new AutomationTaskCompiler();
        foreach (var p in plugins) { foreach (var d in p.Detections) compiler.Register(d); foreach (var a in p.Actions) compiler.Register(a); }
        var catalog = new AutomationSnapshot([], compiler.Catalog, [.. plugins.SelectMany(p => p.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "application-audio-awake"); Assert.True(template.Draft.Paused);
        Assert.Throws<ArgumentException>(() => compiler.Compile(template.Draft, "owner", ResourceScope.Session("owner", 1)));
        var paths = App() + "\n" + App("SyntheticConference"); var draft = template.Draft with { Detection = Detection(paths) };
        var compiled = compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)); Assert.Equal(2, compiled.Condition.Dependencies.Count());
        Assert.Equal(RuleActionKind.Desired, compiled.Action.Kind);
        Assert.Equal(new[] { KeepAwakeCapabilities.Hold, NotificationCapabilities.Show, TaskbarAutoHideRestoreCapabilities.Restore }.Order(), catalog.Combinations.Where(c => c.DetectionId == ApplicationAudioActivityCapabilities.Active).Select(c => c.ActionId).Order());
        var now = DateTimeOffset.UtcNow; var recorder = new UsageRecorder(new(5, false, true, [], SystemEvents: true), now);
        recorder.Add(new(now, "process-start", App())); recorder.Add(new(now.AddSeconds(1), "process-start", App("SyntheticConference")));
        var aliases = string.Join("\n", recorder.Session.Objects.Select(o => o.Id));
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "合成播放检测", "合成导入", Detection(aliases), draft.Action);
        var imported = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(imported.Paused); Assert.Equal(2, ApplicationAudioActivityCapabilities.Validate(imported.Detection).Length);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection(paths) }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => ApplicationAudioActivityCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.Add("state", "active") }));
        Assert.Throws<ArgumentException>(() => ApplicationAudioActivityCapabilities.Validate(Detection(string.Join("\n", Enumerable.Range(0, 33).Select(i => App("Synthetic" + i))))));
        var db = Path.Combine(Path.GetTempPath(), "axiom-application-audio-" + Guid.NewGuid().ToString("N") + ".db");
        try { using var store = new SqliteAutomationTaskStore(db); await store.SaveAsync(imported, default); Assert.Equal(imported.Detection.Parameters["executable"], Assert.Single(await store.ListAsync()).Detection.Parameters["executable"]); }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(db + suffix); }
    }
    private sealed class Session : IApplicationAudioActivitySession
    {
        internal bool Exited; internal int AudioState = 1; internal AudioSessionIdentity Identity = AudioSessionIdentity.Owned;
        internal string? Path; internal int Watches, Identifications;
        public bool ProcessExited => Exited; public int State => AudioState;
        public AudioSessionIdentity Identify(out string? path) { Identifications++; path = Path; return Identity; }
        public void WatchExit() => Watches++;
    }
    private sealed class Api : IApplicationAudioActivityApi
    {
        internal Action<ApplicationAudioHint>? Notify; internal Action? BeforeRead; internal ApplicationAudioReading[] Values = [];
        internal int Disposals, ReadThread, DisposeThread;
        internal void Emit() => Notify!(new("read"));
        public ApplicationAudioReading[] Read(ApplicationAudioHint hint, long selection, string[] paths)
        { ReadThread = Environment.CurrentManagedThreadId; var values = Values; BeforeRead?.Invoke(); return values; }
        public void Dispose() { DisposeThread = Environment.CurrentManagedThreadId; Disposals++; }
    }
    private sealed class Payload : IDisposable { internal bool Disposed; public void Dispose() => Disposed = true; }
    private sealed class Executor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        internal bool Authorized; internal int Commands;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) { if (key == AutomationFixture.Output) Authorized = authorized; }
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic lease", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) { Commands++; return ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic command", command.CommandId)); }
    }
}
