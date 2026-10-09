using System.Collections.Immutable;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class SessionDisplayTests
{
    private static TaskInvocation Detection(string state = "off") => new(SessionDisplayCapabilities.State, 1,
        ImmutableDictionary<string, string>.Empty.Add("state", state));
    [Theory]
    [InlineData("on", 1u, true)] [InlineData("on", 0u, false)] [InlineData("on", 2u, false)]
    [InlineData("off", 0u, true)] [InlineData("off", 1u, false)] [InlineData("off", 2u, false)]
    [InlineData("dim", 2u, true)] [InlineData("dim", 0u, false)] [InlineData("dim", 1u, false)]
    public void EachDocumentedReportSatisfiesOnlyItsExactState(string resource, uint value, bool expected)
        => Assert.Equal(expected, SessionDisplayState.Evaluate(resource, value));
    [Theory]
    [InlineData(null)] [InlineData(3u)] [InlineData(uint.MaxValue)]
    public void MissingOrUnrecognizedPayloadNeverMeansOff(uint? value)
    {
        foreach (var resource in new[] { "on", "dim", "off" }) Assert.Null(SessionDisplayState.Evaluate(resource, value));
        Assert.Null(new SessionDisplayState().Read(value));
    }
    [Theory]
    [InlineData("")] [InlineData("On")] [InlineData("locked")] [InlineData("3")]
    public void OnlyDeclaredStatesAreAccepted(string state) => Assert.Throws<ArgumentException>(() => SessionDisplayCapabilities.Validate(Detection(state)));
    [Fact]
    public void FirstAndRecoveredReportsAreBaselinesAndDuplicateValuesAreSilent()
    {
        var state = new SessionDisplayState(); Assert.Null(state.Read(null));
        Assert.True(state.Read(0)!.Baseline); Assert.Null(state.Read(0));
        Assert.False(state.Read(1)!.Baseline); Assert.False(state.Read(2)!.Baseline);
        Assert.Null(state.Read(2)); var lost = state.Read(255); Assert.Null(lost!.State); Assert.False(lost.Baseline);
        Assert.Null(state.Read(null)); Assert.True(state.Read(0)!.Baseline);
    }
    [Fact]
    public async Task DeclarationPersistsExactlyAndRejectsFeedbackCombinations()
    {
        var plugin = new SessionDisplayPlugin(); Assert.Empty(plugin.Actions); Assert.Single(plugin.Bindings);
        var condition = new SessionDisplayTaskDetection().Compile(Detection(), ResourceScope.Session("owner", 1));
        Assert.Equal("off", Assert.Single(condition.Dependencies).ResourceId);
        Assert.All(plugin.Combinations, p => Assert.Contains(p.ActionId, new[] { AudioPlaybackCapabilities.Set, MicrophoneCapabilities.Set, CurrentMediaCapabilities.Control, NotificationCapabilities.Show, "program.close" }));
        Assert.DoesNotContain(plugin.Combinations, p => p.ActionId == KeepAwakeCapabilities.Hold || p.ActionId == BuiltInBrightnessCapabilities.Set || p.ActionId == WindowMonitorCapabilities.Move);
        Assert.Throws<ArgumentException>(() => SessionDisplayCapabilities.Validate(Detection() with { SchemaVersion = 2 }));
        Assert.Throws<ArgumentException>(() => SessionDisplayCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.Add("screen", "synthetic") }));
        var path = Path.Combine(Path.GetTempPath(), "axiom-screen-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var task = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "synthetic screen off", true, Detection(),
                new(AudioPlaybackCapabilities.Set, 1, ImmutableDictionary<string, string>.Empty.Add("operation", "mute").Add("percent", "30").Add("delaySeconds", "0")));
            using var store = new SqliteAutomationTaskStore(path); await store.SaveAsync(task, default);
            var saved = Assert.Single(await store.ListAsync(default)); Assert.True(saved.Paused); Assert.Equal("off", saved.Detection.Parameters["state"]);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    [Fact(Timeout = 10000)]
    public async Task NoNotificationRemainsUnknownAndLockOrTopologyDoesNotInferAScreenState()
    {
        var source = new FakeSource(); using var stop = new CancellationTokenSource();
        var updates = Channel.CreateBounded<ProviderUpdate>(16);
        var provider = new SessionDisplayCapabilityProvider(() => source);
        var run = provider.ObserveAsync([new(SessionDisplayCapabilities.State, "off")], u => Assert.True(updates.Writer.TryWrite(u)), stop.Token);
        var initial = await updates.Reader.ReadAsync(); Assert.True(initial.Snapshot); Assert.Equal(StateQuality.Unknown, initial.Samples[0].Quality);
        foreach (var kind in new[] { "locked", "unlocked", "display-changed", "resumed" }) source.Emit(kind);
        source.Emit("screen-off"); var first = await updates.Reader.ReadAsync(); Assert.True(first.Snapshot); Assert.True(first.Samples[0].Boolean);
        Assert.False(updates.Reader.TryRead(out _)); Assert.False(run.IsCompleted);
        source.Emit("screen-on"); var on = await updates.Reader.ReadAsync(); Assert.False(on.Snapshot); Assert.False(on.Samples[0].Boolean);
        source.Emit("screen-off"); var off = await updates.Reader.ReadAsync(); Assert.False(off.Snapshot); Assert.True(off.Samples[0].Boolean);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.Equal(1, source.Disposals);
    }
    [Fact(Timeout = 10000)]
    public async Task ResetMakesAllResourcesUnknownAndTheNextReportOnlyEstablishesABaseline()
    {
        var source = new FakeSource(); using var stop = new CancellationTokenSource(); var updates = Channel.CreateBounded<ProviderUpdate>(16);
        var run = new SessionDisplayCapabilityProvider(() => source).ObserveAsync([new(SessionDisplayCapabilities.State, "on"), new(SessionDisplayCapabilities.State, "dim"), new(SessionDisplayCapabilities.State, "off")],
            u => Assert.True(updates.Writer.TryWrite(u)), stop.Token);
        await updates.Reader.ReadAsync(); source.Emit("screen-on"); var first = await updates.Reader.ReadAsync(); Assert.True(first.Snapshot);
        Assert.Equal(new bool?[] { true, false, false }, first.Samples.Select(s => s.Boolean));
        source.Emit("screen-reset"); var reset = await updates.Reader.ReadAsync(); Assert.False(reset.Snapshot); Assert.All(reset.Samples, s => Assert.Equal(StateQuality.Unknown, s.Quality));
        source.Emit("screen-off"); var baseline = await updates.Reader.ReadAsync(); Assert.True(baseline.Snapshot); Assert.Equal(new bool?[] { false, false, true }, baseline.Samples.Select(s => s.Boolean));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
    [Fact(Timeout = 10000)]
    public async Task LostSourceDropsEveryBufferedKnownAndReactivationWaitsForAFreshReport()
    {
        var failed = new FakeSource(); for (var i = 0; i < 64; i++) failed.Emit(i % 2 == 0 ? "screen-on" : "screen-off"); failed.Fail();
        var fresh = new FakeSource(); var connections = 0; var provider = new SessionDisplayCapabilityProvider(() => ++connections == 1 ? failed : fresh);
        var updates = Channel.CreateBounded<ProviderUpdate>(8); using var firstStop = new CancellationTokenSource();
        var first = provider.ObserveAsync([new(SessionDisplayCapabilities.State, "off")], u => Assert.True(updates.Writer.TryWrite(u)), firstStop.Token);
        await updates.Reader.ReadAsync(); var fault = await updates.Reader.ReadAsync(); Assert.Equal(StateQuality.Unknown, fault.Samples[0].Quality);
        Assert.False(updates.Reader.TryRead(out _)); await failed.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(1, failed.Disposals); Assert.False(first.IsCompleted);
        firstStop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        using var secondStop = new CancellationTokenSource();
        var second = provider.ObserveAsync([new(SessionDisplayCapabilities.State, "off")], u => Assert.True(updates.Writer.TryWrite(u)), secondStop.Token);
        Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync()).Samples[0].Quality);
        fresh.Emit("screen-off"); Assert.True((await updates.Reader.ReadAsync()).Snapshot); Assert.Equal(2, connections);
        secondStop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }
    [Fact(Timeout = 10000)]
    public async Task UnknownIsPublishedBeforeFailedNativeCleanupCanBlock()
    {
        using var stop = new CancellationTokenSource(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var source = new FakeSource(); source.Fail(); source.Disposing = () => { entered.Set(); release.Wait(); };
        var updates = Channel.CreateBounded<ProviderUpdate>(8);
        var run = Task.Run(() => new SessionDisplayCapabilityProvider(() => source).ObserveAsync([new(SessionDisplayCapabilities.State, "off")], u => Assert.True(updates.Writer.TryWrite(u)), stop.Token));
        try
        {
            await updates.Reader.ReadAsync(); var unknown = await updates.Reader.ReadAsync(); Assert.Equal(StateQuality.Unknown, unknown.Samples[0].Quality);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2))); Assert.False(run.IsCompleted);
        }
        finally { release.Set(); stop.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.Equal(1, source.Disposals);
    }
    [Theory(Timeout = 10000)]
    [InlineData("on")] [InlineData("dim")] [InlineData("off")]
    public async Task EngineCommandsOnlyRunOnNewMatchesAndNeverReplayInitialRecoveryOrPausedState(string selected)
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(SessionDisplayCapabilities.State, schema: schema));
        var condition = new SessionDisplayTaskDetection().Compile(Detection(selected), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-screen", [key]); long sequence = 0;
        var rule = new RuleDefinition("synthetic-screen-rule", 1, "owner", ResourceScope.Machine, condition, RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        void Set(bool? value, bool baseline = false)
        {
            var observation = new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown, value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        Set(true, true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(false); Set(true); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(null); Set(true, true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", rule.Scope, condition, rule.Action, enabled: false)); Set(false); Set(true);
        fixture.Engine.SetRule(new(rule.Id, 3, "owner", rule.Scope, condition, rule.Action)); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(false); Set(true); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
    }
    [Fact]
    public async Task CancelledOrInvalidObservationDoesNotStartTheNativeSource()
    {
        var starts = 0; var provider = new SessionDisplayCapabilityProvider(() => { starts++; return new FakeSource(); });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ObserveAsync([new(SessionDisplayCapabilities.State, "off")], _ => throw new InvalidOperationException(), cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ObserveAsync([new(SessionDisplayCapabilities.State, "locked")], _ => { }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ObserveAsync([new(SessionDisplayCapabilities.State, "off"), new(SessionDisplayCapabilities.State, "off")], _ => { }, default));
        Assert.Equal(0, starts);
    }
    private sealed class FakeSource : ISessionDisplaySource
    {
        public readonly Channel<string> Queue = Channel.CreateBounded<string>(64);
        public ChannelReader<string> Events => Queue.Reader;
        public bool Failed { get; private set; }
        public readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals; public Action? Disposing;
        public void Emit(string kind) => Assert.True(Queue.Writer.TryWrite(kind));
        public void Fail() { Failed = true; Queue.Writer.TryComplete(new IOException("synthetic lost notification")); }
        public void Dispose() { Disposals++; Disposing?.Invoke(); Queue.Writer.TryComplete(); Disposed.TrySetResult(); }
    }
}
