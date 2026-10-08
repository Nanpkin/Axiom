using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class ServiceStatusTests
{
    private static readonly ResourceKey Key = new("windows.service.status", "EventLog", ResourceScope.Machine);

    [Fact]
    public void DisplayDemandUsesEngineStateAndReleasesLastReference()
    {
        using var fixture = new Fixture();
        var engine = fixture.Engine;
        engine.ConfigureDisplayObservation(Key);
        Assert.Equal(StateQuality.Unknown, engine.ReadDisplayObservation().State!.Observation.Quality);
        using var one = engine.AcquireDisplayObservation(Key);
        using var two = engine.AcquireDisplayObservation(Key);
        Assert.Equal(2, engine.DisplayDemandCount);
        engine.PublishDisplaySample(Key, StateQuality.Known,
            StateValue.From(new ValueSchema("windows.service.status", 1), "running"));
        Assert.Equal("running", engine.ReadSnapshot().States[Key].Observation.Value!.Text);
        Assert.Equal(fixture.Clock.UtcNow, engine.ReadDisplayObservation().LastSuccessAt);
        one.Dispose();
        Assert.Equal(1, engine.DisplayDemandCount);
        two.Dispose();
        Assert.Equal(0, engine.DisplayDemandCount);
        Assert.Equal(StateQuality.Unknown, engine.ReadSnapshot().States[Key].Observation.Quality);
        Assert.Null(engine.ReadSnapshot().States[Key].Observation.Value);
    }

    [Fact]
    public void DisplayResourceRejectsScopeAndForeignDemand()
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Engine.ConfigureDisplayObservation(
            new ResourceKey("windows.service.status", "EventLog", ResourceScope.User("other"))));
        fixture.Engine.ConfigureDisplayObservation(Key);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Engine.AcquireDisplayObservation(
            new ResourceKey("windows.service.status", "Other", ResourceScope.Machine)));
        fixture.Engine.PublishDisplaySample(Key, StateQuality.Known,
            StateValue.From(new ValueSchema("windows.service.status", 1), "running"));
        Assert.Equal(StateQuality.Unknown, fixture.Engine.ReadSnapshot().States[Key].Observation.Quality);
    }

    [Theory]
    [InlineData(1, "stopped")]
    [InlineData(2, "start-pending")]
    [InlineData(3, "stop-pending")]
    [InlineData(4, "running")]
    [InlineData(5, "continue-pending")]
    [InlineData(6, "pause-pending")]
    [InlineData(7, "paused")]
    [InlineData(0, null)]
    [InlineData(8, null)]
    public void NativeStateMappingIsClosed(uint input, string? expected) =>
        Assert.Equal(expected, ServiceStateNames.FromNative(input));

    [Fact]
    public async Task NativeDirtyDuringQueryRunsAnotherSerialQueryAndStopsCleanly()
    {
        var fake = new BlockingNative();
        var observer = new ServiceStatusObserver(fake);
        var twoSamples = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new List<ServiceStatusSample>();
        await observer.StartAsync(sample =>
        {
            lock (samples) { samples.Add(sample); if (samples.Count == 2) twoSamples.TrySetResult(); }
        });
        await fake.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100; i++) fake.Notify(0); // zero bitmask and storm coalesce
        fake.UnblockFirstQuery.Set();
        await twoSamples.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fake.Queries);
        Assert.Equal(1, fake.MaximumConcurrentQueries);
        Assert.Equal(new[] { "running", "stopped" }, samples.Select(x => x.State));
        await observer.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fake.Unsubscriptions);
        Assert.Equal(1, fake.Closes);
        fake.Notify(0); // old generation callback cannot schedule another query
        Assert.Equal(2, fake.Queries);
    }

    [Fact]
    public async Task SubscribeCallbackAndExplicitRetryDoNotCreatePolling()
    {
        var fake = new BlockingNative { NotifyDuringSubscribe = true, FailQuery = true };
        var observer = new ServiceStatusObserver(fake);
        var first = new TaskCompletionSource<ServiceStatusSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        await observer.StartAsync(sample => first.TrySetResult(sample));
        Assert.Contains("QueryServiceStatusEx", (await first.Task.WaitAsync(TimeSpan.FromSeconds(5))).Error);
        await observer.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fake.Queries);
        fake.FailQuery = false;
        var second = new TaskCompletionSource<ServiceStatusSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        await observer.StartAsync(sample => second.TrySetResult(sample));
        Assert.Equal("stopped", (await second.Task.WaitAsync(TimeSpan.FromSeconds(5))).State);
        await observer.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fake.Queries);
        Assert.Equal(2, fake.Subscriptions);
        Assert.Equal(2, fake.Unsubscriptions);
    }

    [Fact]
    public async Task StopKeepsNativeOwnershipUntilBlockedQueryAndUnsubscribeFinish()
    {
        var fake = new BlockingNative { BlockUnsubscribe = true };
        var observer = new ServiceStatusObserver(fake);
        await observer.StartAsync(_ => { });
        await fake.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = observer.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.Equal(0, fake.Closes);
        fake.UnblockFirstQuery.Set();
        await fake.UnsubscribeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        Assert.Equal(0, fake.Closes);
        fake.UnblockUnsubscribe.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fake.Closes);
    }

    [Fact]
    public async Task PartialAcquireFailureClosesOpenedHandlesAndReportsStage()
    {
        var fake = new BlockingNative { FailSubscribe = true };
        var observer = new ServiceStatusObserver(fake);
        var reported = new TaskCompletionSource<ServiceStatusSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        await observer.StartAsync(sample => reported.TrySetResult(sample));
        Assert.Contains("SubscribeServiceChangeNotifications", (await reported.Task.WaitAsync(TimeSpan.FromSeconds(5))).Error);
        await observer.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fake.Closes);
        Assert.Equal(0, fake.Unsubscriptions);
    }

    [Fact]
    public async Task StopDuringOpenReturnsPendingTaskAndNeverSubscribesAfterDemandEnds()
    {
        var fake = new BlockingNative { BlockOpen = true };
        var observer = new ServiceStatusObserver(fake);
        await observer.StartAsync(_ => { }); // native open is never run on this caller thread
        await fake.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = observer.StopAsync();
        Assert.False(stop.IsCompleted);
        fake.UnblockOpen.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fake.Subscriptions);
        Assert.Equal(1, fake.Closes);
    }

    [Fact]
    public async Task ProtocolRejectsOversizeVersionScopeAndUnknownOperation()
    {
        var now = DateTimeOffset.UnixEpoch;
        var request = new ServiceMessage(1, 0, 1, "observe", "id", "machine", "eventlog", DeadlineUtc: now.AddSeconds(10));
        ServiceStatusProtocol.ValidateRequest(request, now);
        Assert.Throws<InvalidDataException>(() => ServiceStatusProtocol.ValidateRequest(request with { Major = 2 }, now));
        Assert.Throws<UnauthorizedAccessException>(() => ServiceStatusProtocol.ValidateRequest(request with { Scope = "session" }, now));
        Assert.Throws<InvalidDataException>(() => ServiceStatusProtocol.ValidateRequest(request with { Type = "stop" }, now));
        Assert.Throws<InvalidDataException>(() => ServiceStatusProtocol.ValidateRequest(request with { DeadlineUtc = now }, now));
        using var stream = new MemoryStream();
        await ServiceStatusProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(request, await ServiceStatusProtocol.ReadAsync(stream, CancellationToken.None));
        stream.Position = 0;
        stream.Write(BitConverter.GetBytes(4097));
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ServiceStatusProtocol.ReadAsync(stream, CancellationToken.None));
    }

    private sealed class BlockingNative : IServiceStatusNative
    {
        private Action<uint>? callback;
        private int activeQueries;
        public TaskCompletionSource QueryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UnsubscribeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim UnblockFirstQuery { get; } = new(false);
        public ManualResetEventSlim UnblockOpen { get; } = new(false);
        public ManualResetEventSlim UnblockUnsubscribe { get; } = new(false);
        public bool NotifyDuringSubscribe { get; set; }
        public bool FailQuery { get; set; }
        public bool FailSubscribe { get; set; }
        public bool BlockUnsubscribe { get; set; }
        public bool BlockOpen { get; set; }
        public int Queries { get; private set; }
        public int MaximumConcurrentQueries { get; private set; }
        public int Subscriptions { get; private set; }
        public int Unsubscriptions { get; private set; }
        public int Closes { get; private set; }
        public ServiceHandles Open()
        {
            if (BlockOpen) { OpenEntered.TrySetResult(); UnblockOpen.Wait(); }
            return new((IntPtr)1, (IntPtr)2);
        }
        public IntPtr Subscribe(IntPtr service, Action<uint> notify)
        {
            Subscriptions++;
            if (FailSubscribe) throw new ServiceNativeException("SubscribeServiceChangeNotifications", 5);
            callback = notify;
            if (NotifyDuringSubscribe) notify(0);
            return (IntPtr)3;
        }
        public uint Query(IntPtr service)
        {
            var concurrent = Interlocked.Increment(ref activeQueries);
            MaximumConcurrentQueries = Math.Max(MaximumConcurrentQueries, concurrent);
            var number = ++Queries;
            try
            {
                if (FailQuery) throw new ServiceNativeException("QueryServiceStatusEx", 5);
                if (number == 1 && !NotifyDuringSubscribe)
                {
                    QueryEntered.TrySetResult();
                    UnblockFirstQuery.Wait();
                }
                return number == 1 ? 4u : 1u;
            }
            finally { Interlocked.Decrement(ref activeQueries); }
        }
        public void Notify(uint bitmask) => callback?.Invoke(bitmask);
        public void Unsubscribe(IntPtr subscription)
        {
            Unsubscriptions++;
            if (BlockUnsubscribe)
            {
                UnsubscribeEntered.TrySetResult();
                UnblockUnsubscribe.Wait();
            }
        }
        public void Close(ServiceHandles handles) => Closes++;
    }
}
