using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class MediaPlaybackTests
{
    private static TaskInvocation Detection(string state = "playing") => new(MediaPlaybackCapabilities.State, 1,
        ImmutableDictionary<string, string>.Empty.Add("state", state));
    private static async Task<MediaPlaybackSample> Read(IMediaPlaybackSource source, MediaPlaybackStatus status)
    {
        while (true) { var sample = await source.Events.ReadAsync(); if (sample.Status == status) return sample; }
    }
    [Fact(Timeout = 10000)]
    public async Task SharedLeasesSubscribeBeforeReadAndOnlyLastConsumerUnsubscribes()
    {
        var api = new Api(new Node(MediaPlaybackStatus.Playing)); var opens = 0;
        var hub = new MediaPlaybackHub(() => { Interlocked.Increment(ref opens); return api; });
        using var first = hub.Subscribe(); Assert.True((await Read(first, MediaPlaybackStatus.Playing)).Baseline);
        using var second = hub.Subscribe(); Assert.True((await Read(second, MediaPlaybackStatus.Playing)).Baseline);
        Assert.Equal(1, opens); Assert.Equal(1, api.Subscriptions); Assert.Equal(1, api.Selected!.Subscriptions);
        first.Dispose(); api.Selected.Status = MediaPlaybackStatus.Paused; api.Selected.Emit();
        Assert.False((await Read(second, MediaPlaybackStatus.Paused)).Baseline); Assert.False(hub.Stopped.Task.IsCompleted);
        api.FailOnDispose = true; // Cleanup failure must finish this source without escaping its worker thread.
        second.Dispose(); await hub.Stopped.Task; Assert.Equal(1, api.Disposals); Assert.Equal(0, api.Subscriptions); Assert.Equal(0, api.Selected.Subscriptions);
    }
    [Fact(Timeout = 10000)]
    public async Task SelectionChangesDuringReadAndLateOldCallbacksNeverPublishStalePlaying()
    {
        var old = new Node(MediaPlaybackStatus.Playing); var next = new Node(MediaPlaybackStatus.Paused); var api = new Api(old);
        var switched = false;
        old.BeforeRead = () => { if (!switched) { switched = true; api.Select(next); } };
        var hub = new MediaPlaybackHub(() => api); using var source = hub.Subscribe();
        var paused = await Read(source, MediaPlaybackStatus.Paused); Assert.True(paused.Baseline);
        Assert.Equal(0, old.Subscriptions); Assert.Equal(1, next.Subscriptions);
        old.LateCallbacks?.Invoke(); // A queued native callback can arrive after unsubscription.
        next.Status = MediaPlaybackStatus.Stopped; next.Emit();
        var stopped = await source.Events.ReadAsync(); Assert.Equal(MediaPlaybackStatus.Stopped, stopped.Status); Assert.False(stopped.Baseline);
        Assert.False(source.Events.TryRead(out _));
    }
    [Fact(Timeout = 10000)]
    public async Task FailedPlaybackQueryRecoversAsBaselineAndNoSessionDoesNotMeanPausedOrStopped()
    {
        var node = new Node(MediaPlaybackStatus.Playing); var api = new Api(node); var hub = new MediaPlaybackHub(() => api); using var source = hub.Subscribe();
        await Read(source, MediaPlaybackStatus.Playing);
        node.BeforeRead = () => throw new IOException("synthetic playback query failed"); node.Emit();
        Assert.Null((await Read(source, MediaPlaybackStatus.Unknown)).Matches("stopped"));
        node.BeforeRead = null; node.Status = MediaPlaybackStatus.Paused; node.Emit();
        Assert.True((await Read(source, MediaPlaybackStatus.Paused)).Baseline);
        api.Select(null); var empty = await Read(source, MediaPlaybackStatus.NoSession); Assert.True(empty.Baseline);
        foreach (var resource in new[] { "playing", "paused", "stopped" }) Assert.False(empty.Matches(resource));
        api.Select(new Node(MediaPlaybackStatus.Stopped)); Assert.True((await Read(source, MediaPlaybackStatus.Stopped)).Baseline);
    }
    [Fact(Timeout = 10000)]
    public async Task HintOverflowDiscardsBufferedKnownAndPublishesUnknownBeforeBlockedCleanup()
    {
        var node = new Node(MediaPlaybackStatus.Playing); var api = new Api(node); var hub = new MediaPlaybackHub(() => api);
        var updates = Channel.CreateBounded<ProviderUpdate>(16); using var stop = new CancellationTokenSource();
        var run = new MediaPlaybackCapabilityProvider(hub.Subscribe).ObserveAsync([new(MediaPlaybackCapabilities.State, "playing")], u => Assert.True(updates.Writer.TryWrite(u)), stop.Token);
        while ((await updates.Reader.ReadAsync()).Samples[0].Quality != StateQuality.Known) { }
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        node.BeforeRead = () => { entered.Set(); release.Wait(); };
        try
        {
            node.Emit(); Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            for (var i = 0; i < 65; i++) node.Emit();
            var unknown = await updates.Reader.ReadAsync(); Assert.Equal(StateQuality.Unknown, unknown.Samples[0].Quality);
            Assert.False(updates.Reader.TryRead(out _)); Assert.False(hub.Stopped.Task.IsCompleted);
        }
        finally { release.Set(); stop.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); await hub.Stopped.Task; Assert.Equal(1, api.Disposals);
    }
    [Fact(Timeout = 10000)]
    public async Task EngineUsesInitialPlayingForDesiredButCommandsNeedNewKnownMatches()
    {
        var executor = new Executor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(MediaPlaybackCapabilities.State, schema: schema));
        var playing = new MediaPlaybackTaskDetection().Compile(Detection(), ResourceScope.Machine);
        var paused = new MediaPlaybackTaskDetection().Compile(Detection("paused"), ResourceScope.Machine);
        var playingKey = Assert.Single(playing.Dependencies); var pausedKey = Assert.Single(paused.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-current-media", [playingKey, pausedKey]); long sequence = 0;
        var command = new ResourceKey("output", "paused-command", ResourceScope.Machine);
        void Set(MediaPlaybackStatus status, bool baseline)
        {
            var sample = new MediaPlaybackSample(status, baseline); var observed = new[] { playingKey, pausedKey }.Select(k =>
            {
                var value = sample.Matches(k.ResourceId); return new Observation(k, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                    value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow);
            }).ToArray();
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, observed);
            else foreach (var observation in observed) fixture.Engine.Update(source, ++sequence, observation);
        }
        Set(MediaPlaybackStatus.Playing, true);
        fixture.Engine.SetRule(new("awake", 1, "owner", ResourceScope.Machine, playing, RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true))));
        fixture.Engine.SetRule(new("pause-command", 1, "owner", ResourceScope.Machine, paused, RuleAction.Command(command, StateValue.From(AutomationFixture.Schema, true))));
        fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized); Assert.Equal(0, executor.Commands);
        fixture.Update(true, AutomationFixture.Output); // The synthetic lease now reports its confirmed held state.
        Set(MediaPlaybackStatus.Unknown, false); Assert.False(executor.Authorized);
        Set(MediaPlaybackStatus.Paused, true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Commands);
        Set(MediaPlaybackStatus.Playing, false); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        Set(MediaPlaybackStatus.Paused, false); await fixture.Engine.WaitForIdleAsync(); Assert.False(executor.Authorized); Assert.Equal(1, executor.Commands);
        Set(MediaPlaybackStatus.NoSession, true); Assert.False(executor.Authorized); Assert.Equal(1, executor.Commands);
    }
    [Fact]
    public async Task RealRegistrationStrictImportAndPausedTemplatePersistWithoutMediaEffects()
    {
        var plugins = BuiltInAutomationPlugins.Create(); var compiler = new AutomationTaskCompiler();
        foreach (var plugin in plugins) { foreach (var d in plugin.Detections) compiler.Register(d); foreach (var a in plugin.Actions) compiler.Register(a); }
        var catalog = new AutomationSnapshot([], compiler.Catalog, [.. plugins.SelectMany(p => p.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "current-media-awake");
        Assert.True(template.Draft.Paused); Assert.Equal(0, template.Draft.Version);
        Assert.Equal(RuleActionKind.Desired, compiler.Compile(template.Draft, "owner", ResourceScope.Session("owner", 1)).Action.Kind);
        Assert.DoesNotContain(catalog.Combinations, c => c.DetectionId == MediaPlaybackCapabilities.State && c.ActionId == CurrentMediaCapabilities.Control);
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "synthetic media state", "synthetic import", template.Draft.Detection, template.Draft.Action);
        var imported = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!; Assert.True(imported.Paused);
        foreach (var invalid in new[] { "none", "PLAYING", "", "changing" })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection(invalid) }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => MediaPlaybackCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.Add("application", "synthetic") }));
        var path = Path.Combine(Path.GetTempPath(), "axiom-playback-" + Guid.NewGuid().ToString("N") + ".db");
        try { using var store = new SqliteAutomationTaskStore(path); await store.SaveAsync(imported, default); Assert.Equal("playing", Assert.Single(await store.ListAsync()).Detection.Parameters["state"]); }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    private sealed class Executor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        internal bool Authorized; internal int Commands;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized)
        { if (key == AutomationFixture.Output) Authorized = authorized; }
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic lease", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) { Commands++; return ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic command", command.CommandId)); }
    }
    private sealed class Api(Node? node) : IMediaPlaybackApi
    {
        private Action? changed;
        internal Node? Selected = node;
        internal int Subscriptions, Disposals;
        internal bool FailOnDispose;
        public event Action CurrentChanged { add { changed += value; Subscriptions++; } remove { changed -= value; Subscriptions--; } }
        public IMediaPlaybackSession? Current() { Assert.True(Subscriptions > 0); return Selected is { } selected ? new Session(selected) : null; }
        internal void Select(Node? selected) { Selected = selected; changed?.Invoke(); }
        public void Dispose() { Interlocked.Increment(ref Disposals); if (FailOnDispose) throw new IOException("synthetic cleanup failed"); }
    }
    private sealed class Node(MediaPlaybackStatus status)
    {
        internal volatile MediaPlaybackStatus Status = status;
        internal Action? Callbacks, LateCallbacks, BeforeRead;
        internal int Subscriptions;
        internal void Emit() => Callbacks?.Invoke();
    }
    private sealed class Session(Node node) : IMediaPlaybackSession
    {
        private Action? handlers;
        public event Action PlaybackChanged { add { handlers += value; node.Callbacks += value; node.LateCallbacks = node.Callbacks; node.Subscriptions++; } remove { handlers -= value; node.Callbacks -= value; node.Subscriptions--; } }
        public MediaPlaybackStatus Read() { Assert.True(node.Subscriptions > 0); node.BeforeRead?.Invoke(); return node.Status; }
        public bool Same(IMediaPlaybackSession other) => other is Session session && ReferenceEquals(node, session.Node);
        private Node Node => node;
        public void Dispose() { if (handlers is not null) { node.Callbacks -= handlers; node.Subscriptions--; handlers = null; } }
    }
}
