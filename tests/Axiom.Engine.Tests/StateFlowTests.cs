using Axiom.Domain;

namespace Axiom.Engine.Tests;

public sealed class StateFlowTests
{
    internal static readonly ResourceKey A = new("test", "a", ResourceScope.Machine);
    internal static readonly ResourceKey B = new("test", "b", ResourceScope.Machine);
    internal static Observation Observe(ResourceKey key, bool value, DateTimeOffset? at = null) =>
        new(key, StateQuality.Known, StateValue.From(new("bool", 1), value), at ?? DateTimeOffset.UnixEpoch);
    internal static SourceSession Ready(Fixture f, params ResourceKey[] keys)
    {
        var source = f.Engine.RegisterSource("source", keys);
        f.Engine.ApplySnapshot(source, 0, keys.Select(k => Observe(k, false)).ToArray());
        return source;
    }

    [Fact]
    public void SnapshotIsRequiredAndMustBeCompleteWithoutPartialMutation()
    {
        using var f = new Fixture();
        var source = f.Engine.RegisterSource("s", [A, B]);
        Assert.All(f.Engine.ReadSnapshot().States.Values, s => Assert.Equal(StateQuality.Unknown, s.Observation.Quality));
        Assert.Equal(UpdateStatus.SnapshotRequired, f.Engine.Update(source, 1, Observe(A, true)).Status);
        Assert.Throws<ArgumentException>(() => f.Engine.ApplySnapshot(source, 0, [Observe(A, true)]));
        Assert.Throws<ArgumentException>(() => f.Engine.ApplySnapshot(source, 0, [Observe(A, true), Observe(A, false)]));
        var applied = f.Engine.ApplySnapshot(source, 0, [Observe(A, true), Observe(B, false)]);
        Assert.Equal(2, applied.ChangedResources);
        Assert.All(f.Engine.ReadSnapshot().States.Values, s => Assert.Equal(applied.Revision, s.Revision));
    }

    [Fact]
    public void DedupAdvancesSequenceAndMetadataButNotRevisionOrEvent()
    {
        using var f = new Fixture();
        var source = Ready(f, A);
        using var sub = f.Engine.Subscribe([A]).Subscription;
        var before = f.Engine.ReadSnapshot();
        var later = DateTimeOffset.UnixEpoch.AddHours(1);
        Assert.Equal(UpdateStatus.Unchanged, f.Engine.Update(source, 1, Observe(A, false, later)).Status);
        var same = f.Engine.ReadSnapshot();
        Assert.Equal(before.Revision, same.Revision);
        Assert.Equal(1, same.States[A].Sequence);
        Assert.Equal(later, same.States[A].Observation.ObservedAt);
        Assert.Equal(0, sub.PendingCount);
        Assert.Equal(UpdateStatus.StaleSequence, f.Engine.Update(source, 1, Observe(A, true)).Status);
        Assert.Equal(UpdateStatus.StaleSequence, f.Engine.Update(source, 0, Observe(A, true)).Status);
        Assert.Equal(UpdateStatus.Accepted, f.Engine.Update(source, 2, Observe(A, true)).Status);
        Assert.True(sub.TryRead(out var delivery));
        Assert.Equal(before.Revision + 1, delivery!.Revision);
        Assert.False(before.States[A].Observation.Value!.Boolean); // Immutable prior snapshot.
    }

    [Fact]
    public void SequenceGapInvalidatesWholeSourceUntilFreshSnapshot()
    {
        using var f = new Fixture();
        var source = Ready(f, A, B);
        using var sub = f.Engine.Subscribe([A, B]).Subscription;
        Assert.Equal(UpdateStatus.Gap, f.Engine.Update(source, 3, Observe(A, true)).Status);
        Assert.All(f.Engine.ReadSnapshot().States.Values, s => { Assert.Equal(StateQuality.Unknown, s.Observation.Quality); Assert.Null(s.Observation.Value); });
        Assert.Equal(2, sub.PendingCount);
        Assert.Equal(UpdateStatus.SnapshotRequired, f.Engine.Update(source, 4, Observe(A, true)).Status);
        Assert.Equal(UpdateStatus.StaleSequence, f.Engine.ApplySnapshot(source, 3, [Observe(A, true), Observe(B, true)]).Status);
        Assert.Equal(UpdateStatus.Accepted, f.Engine.ApplySnapshot(source, 4, [Observe(A, true), Observe(B, true)]).Status);
        Assert.Equal(UpdateStatus.Accepted, f.Engine.Update(source, 5, Observe(B, false)).Status);
    }

    [Fact]
    public void DisconnectAndRehandshakeRejectOldEpochAndRequireSnapshot()
    {
        using var f = new Fixture();
        var old = Ready(f, A);
        f.Engine.Disconnect(old);
        Assert.Equal(StateQuality.Unknown, f.Engine.ReadSnapshot().States[A].Observation.Quality);
        Assert.Equal(UpdateStatus.StaleEpoch, f.Engine.ApplySnapshot(old, 2, [Observe(A, true)]).Status);
        var current = f.Engine.RegisterSource("source", [A]);
        Assert.True(current.Epoch > old.Epoch);
        Assert.Equal(UpdateStatus.StaleEpoch, f.Engine.Update(old, 1, Observe(A, true)).Status);
        Assert.Equal(UpdateStatus.StaleEpoch, f.Engine.Update(new("source", current.Epoch + 1), 1, Observe(A, true)).Status);
        Assert.Equal(UpdateStatus.SnapshotRequired, f.Engine.Update(current, 1, Observe(A, true)).Status);
        Assert.Equal(UpdateStatus.Accepted, f.Engine.ApplySnapshot(current, 0, [Observe(A, false)]).Status);
    }

    [Fact]
    public void SourceCannotWriteAnotherScopeOrStealOwnership()
    {
        using var f = new Fixture();
        var source = Ready(f, A);
        Assert.Throws<ArgumentException>(() => f.Engine.RegisterSource("thief", [A]));
        Assert.Throws<ArgumentException>(() => f.Engine.RegisterSource("source", [B]));
        Assert.Throws<ArgumentException>(() => f.Engine.Update(source, 1, Observe(B, true)));
        Assert.Equal(UpdateStatus.Accepted, f.Engine.Update(source, 1, Observe(A, true)).Status);
    }

    [Theory]
    [InlineData(EventFidelity.EdgeSensitive)]
    [InlineData(EventFidelity.LatestValue)]
    public async Task OverflowIsBoundedExplicitGapAndRequiresResubscription(EventFidelity fidelity)
    {
        using var f = new Fixture();
        var source = Ready(f, A);
        using var sub = f.Engine.Subscribe([A], 2, fidelity).Subscription;
        for (var i = 1; i <= 1000; i++) f.Engine.Update(source, i, Observe(A, i % 2 != 0));
        Assert.Equal(2, sub.HighWatermark);
        Assert.Equal(1, sub.PendingCount);
        Assert.Equal(3, sub.Dropped);
        Assert.True(sub.NeedsSnapshot);
        Assert.Equal(0, f.Engine.ActiveSubscriptions);
        var gap = await sub.ReadAsync();
        Assert.Equal(DeliveryKind.SnapshotRequired, gap.Kind);
        Assert.Null(gap.State);
        Assert.Equal("CapacityExceeded", gap.Reason);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sub.ReadAsync());
        var resumed = f.Engine.Subscribe([A], 2, fidelity);
        using var newSub = resumed.Subscription;
        Assert.False(resumed.Snapshot.States[A].Observation.Value!.Boolean);
        Assert.Equal(0, newSub.PendingCount);
        f.Engine.Update(source, 1001, Observe(A, true));
        Assert.Equal(DeliveryKind.Changed, (await newSub.ReadAsync()).Kind);
    }

    [Fact]
    public async Task EdgeSensitiveFalseUnknownAndTrueAreNeverMerged()
    {
        using var f = new Fixture();
        var source = Ready(f, A);
        using var sub = f.Engine.Subscribe([A], 4).Subscription;
        f.Engine.Update(source, 1, Observe(A, true));
        f.Engine.Update(source, 2, new(A, StateQuality.Unknown, null, f.Clock.UtcNow));
        f.Engine.Update(source, 3, Observe(A, false));
        f.Engine.Update(source, 4, Observe(A, true));
        Assert.True((await sub.ReadAsync()).State!.Observation.Value!.Boolean);
        Assert.Equal(StateQuality.Unknown, (await sub.ReadAsync()).State!.Observation.Quality);
        Assert.False((await sub.ReadAsync()).State!.Observation.Value!.Boolean);
        Assert.True((await sub.ReadAsync()).State!.Observation.Value!.Boolean);
        Assert.Equal(0, sub.Dropped);
    }

    [Fact]
    public async Task ReadCancellationDoesNotLoseSubscriptionAndDisposeWakesPendingRead()
    {
        using var f = new Fixture();
        var source = Ready(f, A);
        var sub = f.Engine.Subscribe([A]).Subscription;
        using var cancellation = new CancellationTokenSource();
        var pending = sub.ReadAsync(cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        f.Engine.Update(source, 1, Observe(A, true));
        Assert.Equal(DeliveryKind.Changed, (await sub.ReadAsync()).Kind);
        var pendingDispose = sub.ReadAsync().AsTask();
        sub.Dispose();
        sub.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pendingDispose);
        Assert.Equal(0, f.Engine.ActiveSubscriptions);
    }

    [Fact]
    public async Task StopBypassesFullDataQueueAndReleasesAllSubscriptions()
    {
        using var f = new Fixture();
        var source = Ready(f, A);
        using var sub = f.Engine.Subscribe([A], 1).Subscription;
        f.Engine.Update(source, 1, Observe(A, true));
        f.Engine.Stop();
        Assert.Equal(0, f.Engine.ActiveSubscriptions);
        Assert.Equal("EngineStopped", (await sub.ReadAsync()).Reason);
        Assert.Throws<InvalidOperationException>(() => f.Engine.Update(source, 2, Observe(A, false)));
        f.Engine.Start();
        Assert.Empty(f.Engine.ReadSnapshot().States);
        var next = f.Engine.RegisterSource("source", [A]);
        Assert.True(next.Epoch > source.Epoch);
        Assert.Equal(UpdateStatus.StaleEpoch, f.Engine.Update(source, 2, Observe(A, false)).Status);
        Assert.Equal(0, f.Scheduler.Pending);
    }

    [Fact]
    public async Task DependencyIndexOnlySelectsMatchingRulesAndEditsRemoveOldEdges()
    {
        using var f = new Fixture();
        var source = Ready(f, A, B);
        for (var i = 0; i < 1000; i++) f.Engine.SetDependencies($"r{i:D4}", [i < 10 ? A : B]);
        Assert.Equal(10, f.Engine.MatchDependencies(A).Length);
        Assert.Equal(990, f.Engine.MatchDependencies(B).Length);
        f.Engine.SetDependencies("r0000", [B]);
        f.Engine.RemoveDependencies("r0001");
        using var sub = f.Engine.Subscribe([A]).Subscription;
        f.Engine.Update(source, 1, Observe(A, true));
        var delivery = await sub.ReadAsync();
        Assert.Equal(Enumerable.Range(2, 8).Select(i => $"r{i:D4}"), delivery.RuleIds);
        Assert.Equal(0, sub.PendingCount);
        f.Engine.Update(source, 2, Observe(B, true));
        Assert.Equal(0, sub.PendingCount);
    }

    [Fact]
    public void SubscriptionLimitAndCapacityAreEnforced()
    {
        using var f = new Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Engine.Subscribe([A], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Engine.Subscribe([A], 65_537));
        Assert.Throws<ArgumentException>(() => f.Engine.Subscribe([]));
        var subscriptions = Enumerable.Range(0, 128).Select(_ => f.Engine.Subscribe([A]).Subscription).ToArray();
        Assert.Throws<InvalidOperationException>(() => f.Engine.Subscribe([A]));
        foreach (var sub in subscriptions) sub.Dispose();
        Assert.Equal(0, f.Engine.ActiveSubscriptions);
    }
}
