using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class InternetConnectivityTests
{
    private static TaskInvocation Detection(bool available = true) => new(InternetConnectivityCapabilities.State, 1,
        ImmutableDictionary<string, string>.Empty.Add("state", available ? "available" : "unavailable"));

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(0x10u, false)]
    [InlineData(0x20u, false)]
    [InlineData(0x100u, false)]
    [InlineData(0x200u, false)]
    [InlineData(0x333u, false)]
    [InlineData(0x40u, true)]
    [InlineData(0x400u, true)]
    [InlineData(0x440u, true)]
    [InlineData(0x773u, true)]
    [InlineData(8u, null)]
    [InlineData(0x448u, null)]
    [InlineData(uint.MaxValue, null)]
    [InlineData(null, null)]
    public void OnlyPublishedInternetFlagsArePositiveAndFailedOrUnrecognizedStatesAreUnknown(uint? flags, bool? available)
        => Assert.Equal(available, InternetConnectivityState.Evaluate(flags));

    [Fact]
    public void InitialRecoveryAndFamilyChangesUseOneBoolBaselineWithoutInventingOfflineEvents()
    {
        var state = new InternetConnectivityState(); var initial = state.Read(0x40)!;
        Assert.True(initial.Baseline); Assert.True(initial.Available);
        Assert.Null(state.Read(0x400)); // IPv4 -> IPv6 Internet still satisfies the same condition
        var offline = state.Read(0x20)!; Assert.False(offline.Baseline); Assert.False(offline.Available);
        var unknown = state.Read(null)!; Assert.False(unknown.Baseline); Assert.Null(unknown.Available);
        Assert.Null(state.Read(null));
        var recovered = state.Read(0)!; Assert.True(recovered.Baseline); Assert.False(recovered.Available);
        var online = state.Read(0x440)!; Assert.False(online.Baseline); Assert.True(online.Available);
    }

    [Fact]
    public void CallbackAbiIsPublicIUnknownAndIgnoresItsConnectivityPayload()
    {
        var callback = typeof(IInternetConnectivityCallback);
        Assert.True(callback.IsPublic); Assert.True(callback.GetCustomAttribute<ComVisibleAttribute>()!.Value);
        Assert.Equal(new Guid("DCB00001-570F-4A9B-8D69-199FDBA5723B"), callback.GUID);
        Assert.Equal(ComInterfaceType.InterfaceIsIUnknown, callback.GetCustomAttribute<InterfaceTypeAttribute>()!.Value);
        var method = Assert.Single(callback.GetMethods());
        Assert.NotEqual(0, (int)(method.GetMethodImplementationFlags() & MethodImplAttributes.PreserveSig));
        Assert.Equal(typeof(int), method.ReturnType); Assert.Equal(typeof(uint), Assert.Single(method.GetParameters()).ParameterType);
        var calls = 0; var sink = new InternetConnectivityCallbackSink(() => calls++);
        Assert.Equal(0, sink.ConnectivityChanged(uint.MaxValue)); Assert.Equal(0, sink.ConnectivityChanged(0)); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ValidatedConfigurationPersistsAndUsesOneSharedReadOnlyResourceForBothConditions()
    {
        var plugin = new InternetConnectivityPlugin(); var detection = new InternetConnectivityTaskDetection(); var scope = ResourceScope.Session("owner", 1);
        var online = detection.Compile(Detection(), scope); var offline = detection.Compile(Detection(false), scope);
        Assert.Equal(Assert.Single(online.Dependencies), Assert.Single(offline.Dependencies));
        Assert.Equal(InternetConnectivityCapabilities.Resource, online.Dependencies.Single().ResourceId);
        Assert.Throws<ArgumentException>(() => detection.Compile(Detection() with { SchemaVersion = 2 }, scope));
        Assert.Throws<ArgumentException>(() => detection.Compile(Detection() with { Parameters = ImmutableDictionary<string, string>.Empty.Add("state", "network-up") }, scope));
        Assert.Throws<ArgumentException>(() => detection.Compile(Detection() with { Parameters = Detection().Parameters.Add("url", "https://example.invalid") }, scope));
        var compiler = new AutomationTaskCompiler(); compiler.Register(detection); compiler.Register(new KeepAwakeTaskAction());
        var task = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "synthetic Windows Internet", true, Detection(),
            new(KeepAwakeCapabilities.Hold, 1, ImmutableDictionary<string, string>.Empty.Add("target", "system")));
        var path = Path.Combine(Path.GetTempPath(), "axiom-internet-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(task, default);
            using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync());
            Assert.Equal(RuleActionKind.Desired, compiler.Compile(loaded, "owner", scope).Action.Kind);
            Assert.Contains(new(InternetConnectivityCapabilities.State, NotificationCapabilities.Show), plugin.Combinations);
            Assert.DoesNotContain(plugin.Combinations, c => c.ActionId is "network.connect" or "network.disconnect" or SessionLockCapabilities.Lock);
            Assert.True(plugin.Bindings.Single().Descriptor.Observe); Assert.False(plugin.Bindings.Single().Descriptor.Command);
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact(Timeout = 10000)]
    public async Task BoundedHintBatchesQueryOneCurrentReportOnTheSameConsumerThreadAndNeverPoll()
    {
        var hints = new InternetConnectivityNotifications(); var updates = Channel.CreateBounded<InternetConnectivitySample>(8);
        using var stop = new CancellationTokenSource(); var queries = 0; var thread = 0;
        for (var i = 0; i < 64; i++) hints.Hint();
        var run = Task.Factory.StartNew(() => InternetConnectivityObservation.Run(hints.Events, () =>
        {
            if (queries == 0) thread = Environment.CurrentManagedThreadId;
            Assert.Equal(thread, Environment.CurrentManagedThreadId); queries++; return queries == 1 ? 0x20u : 0x40u;
        }, value => Assert.True(updates.Writer.TryWrite(value)), stop.Token, () => hints.Failed), TaskCreationOptions.LongRunning);
        var initial = await updates.Reader.ReadAsync(); Assert.True(initial.Baseline); Assert.False(initial.Available);
        var changed = await updates.Reader.ReadAsync(); Assert.False(changed.Baseline); Assert.True(changed.Available);
        Assert.Equal(2, queries); Assert.False(updates.Reader.TryRead(out _));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.Equal(2, queries);
    }

    [Fact(Timeout = 10000)]
    public async Task QueryFailureWaitsForNotificationsAndRecoveryPublishesABaseline()
    {
        var hints = new InternetConnectivityNotifications(); var updates = Channel.CreateBounded<InternetConnectivitySample>(8);
        using var stop = new CancellationTokenSource(); var flags = 0x20u; var failure = false;
        var run = Task.Factory.StartNew(() => InternetConnectivityObservation.Run(hints.Events,
            () => failure ? throw new COMException("synthetic unavailable query") : flags,
            value => Assert.True(updates.Writer.TryWrite(value)), stop.Token, () => hints.Failed), TaskCreationOptions.LongRunning);
        Assert.True((await updates.Reader.ReadAsync()).Baseline);
        failure = true; hints.Hint(); var unknown = await updates.Reader.ReadAsync(); Assert.Null(unknown.Available); Assert.False(unknown.Baseline);
        failure = false; flags = 0x40; hints.Hint(); var recovered = await updates.Reader.ReadAsync(); Assert.True(recovered.Available); Assert.True(recovered.Baseline);
        flags = 0x20; hints.Hint(); var changed = await updates.Reader.ReadAsync(); Assert.False(changed.Available); Assert.False(changed.Baseline);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact(Timeout = 10000)]
    public async Task OverflowFailsBeforeQueryAndOverflowDuringQueryCannotPublishKnownState()
    {
        var full = new InternetConnectivityNotifications(); for (var i = 0; i < 65; i++) full.Hint(); Assert.True(full.Failed);
        Assert.Throws<IOException>(() => InternetConnectivityObservation.Run(full.Events, () => throw new InvalidOperationException("must not query"), _ => throw new InvalidOperationException("must not publish"), default, () => full.Failed));
        var hints = new InternetConnectivityNotifications(); var samples = Channel.CreateBounded<InternetConnectivitySample>(8); var queries = 0;
        var run = Task.Factory.StartNew(() => InternetConnectivityObservation.Run(hints.Events, () =>
        {
            if (++queries == 2) for (var i = 0; i < 65; i++) hints.Hint();
            return 0x40;
        }, value => Assert.True(samples.Writer.TryWrite(value)), default, () => hints.Failed), TaskCreationOptions.LongRunning);
        Assert.True((await samples.Reader.ReadAsync()).Baseline); hints.Hint();
        await Assert.ThrowsAsync<IOException>(() => run); Assert.False(samples.Reader.TryRead(out _)); Assert.Equal(2, queries);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSubscriptionOrDisconnectedSourceRemainsUnknownAndReleasesOnlyItsLease(bool afterKnown)
    {
        var source = new SyntheticSource(); var updates = Channel.CreateBounded<ProviderUpdate>(8); using var stop = new CancellationTokenSource();
        var provider = new InternetConnectivityCapabilityProvider(() => afterKnown ? source : throw new COMException("synthetic failed advise"));
        var run = provider.ObserveAsync([new(InternetConnectivityCapabilities.State, InternetConnectivityCapabilities.Resource)],
            u => Assert.True(updates.Writer.TryWrite(u)), stop.Token);
        var first = await updates.Reader.ReadAsync(); Assert.True(first.Snapshot); Assert.Equal(StateQuality.Unknown, first.Samples[0].Quality);
        if (afterKnown)
        {
            source.Queue.Writer.TryWrite(new(true, true)); Assert.True((await updates.Reader.ReadAsync()).Snapshot);
            source.Queue.Writer.TryComplete(new IOException("synthetic lost subscription"));
        }
        var failure = await updates.Reader.ReadAsync(); Assert.False(failure.Snapshot); Assert.Equal(StateQuality.Unknown, failure.Samples[0].Quality);
        if (afterKnown) await source.DisposalCompleted.Task;
        Assert.Null(failure.Samples[0].Boolean); Assert.Equal(afterKnown ? 1 : 0, source.Disposed); Assert.False(run.IsCompleted);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(afterKnown ? 1 : 0, source.Disposed);
    }

    [Fact(Timeout = 10000)]
    public async Task OverflowSignalsUnknownWithoutWaitingForABlockedNativeQueryToReturn()
    {
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hints = new InternetConnectivityNotifications(() => lost.TrySetResult());
        var samples = Channel.CreateBounded<InternetConnectivitySample>(8); var queries = 0;
        using var queryEntered = new ManualResetEventSlim(); using var releaseQuery = new ManualResetEventSlim();
        var run = Task.Factory.StartNew(() => InternetConnectivityObservation.Run(hints.Events, () =>
        {
            if (++queries == 2) { queryEntered.Set(); releaseQuery.Wait(); }
            return 0x40;
        }, value => Assert.True(samples.Writer.TryWrite(value)), default, () => hints.Failed), TaskCreationOptions.LongRunning);
        try
        {
            await samples.Reader.ReadAsync(); hints.Hint(); Assert.True(queryEntered.Wait(TimeSpan.FromSeconds(2)));
            for (var i = 0; i < 65; i++) hints.Hint();
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.False(run.IsCompleted); Assert.False(samples.Reader.TryRead(out _));
        }
        finally { releaseQuery.Set(); }
        await Assert.ThrowsAsync<IOException>(() => run); Assert.Equal(2, queries);
    }

    [Fact(Timeout = 10000)]
    public async Task FailedSourceDropsBufferedKnownAndReenabledSourceStartsWithANewBaseline()
    {
        var failed = new SyntheticSource();
        for (var i = 0; i < 64; i++) Assert.True(failed.Queue.Writer.TryWrite(new(i % 2 == 0, i == 0)));
        failed.Failed = true; failed.Queue.Writer.TryComplete(new IOException("synthetic consumer overflow"));
        var fresh = new SyntheticSource(); var connections = 0;
        var provider = new InternetConnectivityCapabilityProvider(() => ++connections == 1 ? failed : fresh);
        var updates = Channel.CreateBounded<ProviderUpdate>(8); using var firstStop = new CancellationTokenSource();
        var first = provider.ObserveAsync([new(InternetConnectivityCapabilities.State, InternetConnectivityCapabilities.Resource)],
            u => Assert.True(updates.Writer.TryWrite(u)), firstStop.Token);
        Assert.True((await updates.Reader.ReadAsync()).Snapshot);
        var fault = await updates.Reader.ReadAsync(); Assert.False(fault.Snapshot); Assert.Equal(StateQuality.Unknown, fault.Samples[0].Quality);
        await failed.DisposalCompleted.Task;
        Assert.False(updates.Reader.TryRead(out _)); Assert.Equal(1, failed.Disposed);
        firstStop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        using var secondStop = new CancellationTokenSource();
        var second = provider.ObserveAsync([new(InternetConnectivityCapabilities.State, InternetConnectivityCapabilities.Resource)],
            u => Assert.True(updates.Writer.TryWrite(u)), secondStop.Token);
        Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync()).Samples[0].Quality);
        fresh.Queue.Writer.TryWrite(new(true, true)); var baseline = await updates.Reader.ReadAsync();
        Assert.True(baseline.Snapshot); Assert.True(baseline.Samples[0].Boolean); Assert.Equal(2, connections);
        secondStop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); Assert.Equal(1, fresh.Disposed);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineOnlyTriggersNewReportsAndNeverReplaysInitialRecoveryOrPausedState(bool available)
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(InternetConnectivityCapabilities.State, schema: schema));
        var condition = new InternetConnectivityTaskDetection().Compile(Detection(available), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-nlm", [key]); long sequence = 0;
        var rule = new RuleDefinition("synthetic-internet-rule", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        void Set(bool? value, bool baseline = false)
        {
            var observation = new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        Set(available, true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(!available); Set(available); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(null); Set(available, true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", rule.Scope, condition, rule.Action, enabled: false)); Set(!available); Set(available);
        fixture.Engine.SetRule(new(rule.Id, 3, "owner", rule.Scope, condition, rule.Action)); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(!available); Set(available); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task InitialKnownInternetHoldsDesiredStateAndOfflineUnknownOrPauseReleasesIt()
    {
        var executor = new SyntheticDesiredExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(InternetConnectivityCapabilities.State, schema: schema));
        var condition = new InternetConnectivityTaskDetection().Compile(Detection(), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-nlm-desired", [key]); long sequence = 0;
        void Set(uint? flags, bool baseline = false)
        {
            var value = InternetConnectivityState.Evaluate(flags);
            var observation = new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++sequence, [observation]); else fixture.Engine.Update(source, ++sequence, observation);
        }
        var rule = new RuleDefinition("synthetic-online-work", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Set(0x40, true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        Set(0x20); Assert.False(executor.Authorized);
        Set(0x400); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        Set(null); Assert.False(executor.Authorized);
        Set(0x40, true); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", rule.Scope, condition, rule.Action, enabled: false)); Assert.False(executor.Authorized);
        fixture.Engine.Stop(); Assert.False(executor.Authorized);
    }

    [Fact]
    public async Task CancelledOrInvalidObservationNeverStartsANativeSource()
    {
        var starts = 0; var provider = new InternetConnectivityCapabilityProvider(() => { starts++; return new SyntheticSource(); });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ObserveAsync([new(InternetConnectivityCapabilities.State, InternetConnectivityCapabilities.Resource)],
            _ => throw new InvalidOperationException("must not publish"), cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ObserveAsync([new(InternetConnectivityCapabilities.State, "interface-name")], _ => { }, default));
        Assert.Equal(0, starts);
    }

    private sealed class SyntheticDesiredExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public bool Authorized;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) => Authorized = authorized;
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token)
            => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic request", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException("Desired must remain Desired.");
    }

    private sealed class SyntheticSource : IInternetConnectivitySource
    {
        public readonly Channel<InternetConnectivitySample> Queue = Channel.CreateBounded<InternetConnectivitySample>(64);
        public ChannelReader<InternetConnectivitySample> Events => Queue.Reader;
        public bool Failed { get; set; }
        public int Disposed;
        public readonly TaskCompletionSource DisposalCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() { Disposed++; Queue.Writer.TryComplete(); DisposalCompleted.TrySetResult(); }
    }
}
