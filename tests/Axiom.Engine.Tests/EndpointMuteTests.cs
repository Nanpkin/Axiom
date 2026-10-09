using System.Collections.Immutable;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class EndpointMuteTests
{
    private static TaskInvocation Detection(string endpoint = "input", string state = "unmuted") => new(EndpointMuteCapabilities.State, 1,
        ImmutableDictionary<string, string>.Empty.Add("endpoint", endpoint).Add("state", state));

    [Theory]
    [InlineData("input", "muted", true)]
    [InlineData("input", "unmuted", false)]
    [InlineData("output", "muted", true)]
    [InlineData("output", "unmuted", false)]
    public void BothRolesCompileToTheSameObservedBooleanResourceWithOppositeConditions(string endpoint, string state, bool expected)
    {
        var condition = new EndpointMuteTaskDetection().Compile(Detection(endpoint, state), ResourceScope.Session("synthetic-owner", 1));
        Assert.Equal(ConditionKind.Equal, condition.Kind); Assert.Equal(endpoint, Assert.Single(condition.Dependencies).ResourceId);
        Assert.Equal(expected, condition.Expected!.Boolean);
        var plugin = new EndpointMutePlugin(); var binding = plugin.Bindings.Single();
        Assert.True(binding.Descriptor.Observe); Assert.False(binding.Descriptor.Command); Assert.False(binding.Descriptor.DesiredState);
        Assert.Contains(new TaskCombination(EndpointMuteCapabilities.State, KeepAwakeCapabilities.Hold), plugin.Combinations);
        Assert.DoesNotContain(plugin.Combinations, c => c.ActionId == MicrophoneCapabilities.Set || c.ActionId == AudioPlaybackCapabilities.Set);
    }

    [Fact]
    public void DeviceIdsExtraParametersAndUnsupportedSchemasAreRejected()
    {
        foreach (var invocation in new[] { Detection("hardware-id"), Detection(state: "toggle"), Detection() with { SchemaVersion = 2 },
            Detection() with { Parameters = Detection().Parameters.Add("deviceId", "synthetic-device") },
            Detection() with { Parameters = Detection().Parameters.Remove("state") } })
            Assert.Throws<ArgumentException>(() => EndpointMuteCapabilities.Validate(invocation));
        Assert.Throws<ArgumentException>(() => new EndpointMuteState("hardware-id"));
    }

    [Fact]
    public void InitialAndReconnectReadsAreBaselinesDuplicatesAreIgnoredAndOldGenerationsAreFenced()
    {
        var state = new EndpointMuteState("input");
        var unknown = state.Reset("synthetic initial"); var first = state.Generation;
        Assert.Null(unknown.Muted); Assert.False(unknown.Baseline);
        var initial = state.Read(first, true)!; Assert.True(initial.Baseline); Assert.True(initial.Muted);
        Assert.Null(state.Read(first, true));
        var transition = state.Read(first, false)!; Assert.False(transition.Baseline); Assert.False(transition.Muted);
        state.Reset("synthetic default replacement"); var next = state.Generation;
        Assert.True(next > first); Assert.Null(state.Read(first, true));
        var reconnect = state.Read(next, true)!; Assert.True(reconnect.Baseline);
        state.Reset("synthetic failure"); Assert.Null(state.Read(next, false));
        Assert.True(state.Read(state.Generation, false)!.Baseline);
    }

    [Fact]
    public void CallbackPayloadsAreNeverDereferenced()
    {
        var signals = 0; var mute = new EndpointMuteCallbackSink(() => signals++);
        Assert.Equal(0, mute.OnNotify(1)); Assert.Equal(0, mute.OnNotify(0)); Assert.Equal(2, signals);
    }

    [Fact(Timeout = 10000)]
    public async Task CoalescedDefaultRoundTripRebaselinesTheSameEndpointAndFencesOldCallbacksButInventoryDoesNot()
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor);
        var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(EndpointMuteCapabilities.State, schema: schema));
        var condition = new EndpointMuteTaskDetection().Compile(Detection(), ResourceScope.Machine);
        var source = fixture.Engine.RegisterSource("synthetic-default-roundtrip", condition.Dependencies); long sequence = 0;
        var state = new EndpointMuteState("input"); var changes = new EndpointMuteDefaultChanges();
        void Publish(EndpointMuteSample sample)
        {
            var observation = new Observation(condition.Key!, sample.Muted.HasValue ? StateQuality.Known : StateQuality.Unknown,
                sample.Muted.HasValue ? StateValue.From(schema, sample.Muted.Value) : null, fixture.Clock.UtcNow);
            if (sample.Baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        Publish(state.Reset("synthetic first endpoint A")); var oldGeneration = state.Generation;
        Publish(state.Read(oldGeneration, true)!); // A is muted, so unmuted command is armed.
        fixture.Engine.SetRule(new("synthetic-roundtrip", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true))));
        fixture.Engine.StartExecution();
        var boundVersion = changes.Version("input");
        changes.Note("input"); changes.Note("input"); // A -> B -> A; one role signal remains after queue coalescing.
        Assert.True(EndpointMuteDefaultChanges.RequiresRebind("synthetic A", "synthetic A", boundVersion, changes.Version("input")));
        Publish(state.Reset("synthetic default selection changed"));
        Assert.Null(state.Read(oldGeneration, false)); // old A callback while A was not the default is stale.
        Publish(state.Read(state.Generation, false)!); // A changed mute state while B was selected; this is a new baseline.
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        boundVersion = changes.Version("input");
        changes.Note("endpoints"); // inventory/sync has no default selection notification.
        Assert.False(EndpointMuteDefaultChanges.RequiresRebind("synthetic A", "synthetic A", boundVersion, changes.Version("input")));
        Assert.True(EndpointMuteDefaultChanges.RequiresRebind("synthetic A", "synthetic B", boundVersion, changes.Version("input")));
        Publish(state.Read(state.Generation, true)!); Publish(state.Read(state.Generation, false)!);
        (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
    }

    [Theory(Timeout = 10000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommandsRequireNewReliableEdgesAndNeverReplayInitialUnknownReconnectOrResume(bool wantedMuted)
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor);
        var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(EndpointMuteCapabilities.State, schema: schema));
        var condition = new EndpointMuteTaskDetection().Compile(Detection(state: wantedMuted ? "muted" : "unmuted"), ResourceScope.Machine);
        var key = condition.Key!; var source = fixture.Engine.RegisterSource("synthetic-mute", [key]); long sequence = 0;
        var rule = new RuleDefinition("synthetic-mute-rule", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        void Set(bool? muted, bool snapshot = false)
        {
            var observation = new Observation(key, muted.HasValue ? StateQuality.Known : StateQuality.Unknown,
                muted.HasValue ? StateValue.From(schema, muted.Value) : null, fixture.Clock.UtcNow);
            if (snapshot) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        Set(wantedMuted, true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution();
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(null); Set(wantedMuted); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(!wantedMuted); Set(wantedMuted); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(null); Set(!wantedMuted, true); Set(wantedMuted, true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new(rule.Id, 2, rule.Principal, rule.Scope, condition, rule.Action, enabled: false));
        Set(!wantedMuted); Set(wantedMuted);
        fixture.Engine.SetRule(new(rule.Id, 3, rule.Principal, rule.Scope, condition, rule.Action));
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(!wantedMuted); Set(wantedMuted); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task InitialUnmutedBaselineCanAuthorizeKeepAwakeAndUnknownRevokesTheLease()
    {
        var executor = new LeaseExecutor(); using var fixture = new AutomationFixture(executor);
        var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(EndpointMuteCapabilities.State, schema: schema));
        var condition = new EndpointMuteTaskDetection().Compile(Detection(), ResourceScope.Machine);
        var source = fixture.Engine.RegisterSource("synthetic-mute", condition.Dependencies); long sequence = 0;
        fixture.Engine.SetRule(new("synthetic-awake", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true))));
        fixture.Engine.StartExecution();
        fixture.Engine.ApplySnapshot(source, ++sequence, [new(condition.Key!, StateQuality.Known, StateValue.From(schema, false), fixture.Clock.UtcNow)]);
        await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        fixture.Update(true, AutomationFixture.Output);
        fixture.Engine.Update(source, ++sequence, new(condition.Key!, StateQuality.Unknown, null, fixture.Clock.UtcNow)); Assert.False(executor.Authorized);
        fixture.Engine.ApplySnapshot(source, ++sequence, [new(condition.Key!, StateQuality.Known, StateValue.From(schema, false), fixture.Clock.UtcNow)]);
        Assert.True(executor.Authorized);
        fixture.Engine.Disconnect(source); Assert.False(executor.Authorized);
    }

    [Fact(Timeout = 10000)]
    public async Task ProviderPreservesBaselinesUnknownAndIndependentResourcesThenDisposesOnCancellation()
    {
        using var lifetime = new CancellationTokenSource(); var fake = new FakeSource();
        var updates = Channel.CreateBounded<ProviderUpdate>(8);
        var provider = new EndpointMuteCapabilityProvider(_ => fake);
        var run = provider.ObserveAsync([new(EndpointMuteCapabilities.State, "input"), new(EndpointMuteCapabilities.State, "output")],
            u => Assert.True(updates.Writer.TryWrite(u)), lifetime.Token);
        var initial = await updates.Reader.ReadAsync(); Assert.True(initial.Snapshot); Assert.Equal(2, initial.Samples.Length); Assert.All(initial.Samples, s => Assert.Equal(StateQuality.Unknown, s.Quality));
        fake.Emit(new("input", false, true)); var baseline = await updates.Reader.ReadAsync(); Assert.True(baseline.Snapshot); Assert.False(Assert.Single(baseline.Samples).Boolean);
        fake.Emit(new("output", true, true)); Assert.Equal("output", Assert.Single((await updates.Reader.ReadAsync()).Samples).Resource.ResourceId);
        fake.Emit(new("input", null, false, "synthetic removed")); var missing = await updates.Reader.ReadAsync(); Assert.False(missing.Snapshot); Assert.Equal(StateQuality.Unknown, Assert.Single(missing.Samples).Quality);
        fake.Emit(new("input", true, true)); Assert.True((await updates.Reader.ReadAsync()).Snapshot);
        fake.Emit(new("input", false, false)); Assert.False((await updates.Reader.ReadAsync()).Snapshot);
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.Equal(1, fake.Disposals);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationAndNotificationFailureRemainUnknownWithoutRetryingOrEndingOtherProviderStreams(bool disconnectAfterActivation)
    {
        using var lifetime = new CancellationTokenSource(); var attempts = 0; var fake = new FakeSource();
        var updates = Channel.CreateBounded<ProviderUpdate>(8);
        var provider = new EndpointMuteCapabilityProvider(_ => { attempts++; if (!disconnectAfterActivation) throw new IOException("synthetic private device identity"); return fake; });
        var run = provider.ObserveAsync([new(EndpointMuteCapabilities.State, "input")], u => Assert.True(updates.Writer.TryWrite(u)), lifetime.Token);
        Assert.True((await updates.Reader.ReadAsync()).Snapshot);
        if (disconnectAfterActivation)
        {
            fake.Emit(new("input", false, true)); Assert.Equal(StateQuality.Known, Assert.Single((await updates.Reader.ReadAsync()).Samples).Quality);
            fake.Fail();
        }
        var failed = await updates.Reader.ReadAsync(); Assert.False(failed.Snapshot); var sample = Assert.Single(failed.Samples);
        Assert.Equal(StateQuality.Unknown, sample.Quality); Assert.Null(sample.Boolean); Assert.DoesNotContain("identity", sample.Reason);
        Assert.False(run.IsCompleted); Assert.Equal(1, attempts);
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(disconnectAfterActivation ? 1 : 0, fake.Disposals);
    }

    private sealed class FakeSource : IEndpointMuteSource
    {
        private readonly Channel<EndpointMuteSample> queue = Channel.CreateBounded<EndpointMuteSample>(8);
        public ChannelReader<EndpointMuteSample> Events => queue.Reader;
        public int Disposals;
        public void Emit(EndpointMuteSample sample) => Assert.True(queue.Writer.TryWrite(sample));
        public void Fail() => queue.Writer.TryComplete(new IOException("synthetic private endpoint identity"));
        public void Dispose() { Disposals++; queue.Writer.TryComplete(); }
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
