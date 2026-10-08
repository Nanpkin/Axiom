using Axiom.Domain;
using static Axiom.Engine.Tests.StateFlowTests;

namespace Axiom.Engine.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task ConcurrentSourcesPublishOneStrictRevisionOrder()
    {
        using var f = new Fixture();
        var keys = Enumerable.Range(0, 16).Select(i => new ResourceKey("c", $"r{i}", ResourceScope.Machine)).ToArray();
        var sources = keys.Select((key, i) => f.Engine.RegisterSource($"s{i}", [key])).ToArray();
        for (var i = 0; i < keys.Length; i++) f.Engine.ApplySnapshot(sources[i], 0, [Observe(keys[i], false)]);
        using var sub = f.Engine.Subscribe(keys, 2048).Subscription;
        await Task.WhenAll(keys.Select((key, i) => Task.Run(() =>
        {
            for (var sequence = 1; sequence <= 100; sequence++)
                Assert.Equal(UpdateStatus.Accepted, f.Engine.Update(sources[i], sequence, Observe(key, sequence % 2 != 0)).Status);
        })));
        long previous = 0;
        var count = 0;
        while (sub.TryRead(out var delivery))
        {
            Assert.True(delivery!.Revision > previous);
            previous = delivery.Revision;
            count++;
        }
        Assert.Equal(1600, count);
        Assert.All(f.Engine.ReadSnapshot().States.Values, s => Assert.Equal(100, s.Sequence));
    }

    [Fact]
    public async Task DuplicateConcurrentUpdateCommitsExactlyOnce()
    {
        using var f = new Fixture();
        var source = Ready(f, A);
        using var sub = f.Engine.Subscribe([A]).Subscription;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => f.Engine.Update(source, 1, Observe(A, true)))));
        Assert.Single(outcomes, x => x.Status == UpdateStatus.Accepted);
        Assert.Equal(31, outcomes.Count(x => x.Status == UpdateStatus.StaleSequence));
        Assert.Equal(1, sub.PendingCount);
    }

    [Fact]
    public async Task AtomicSubscribeHasEitherNewSnapshotOrDeltaWithNoWindow()
    {
        using var f = new Fixture();
        var source = Ready(f, A);
        for (var sequence = 1; sequence <= 100; sequence++)
        {
            var value = sequence % 2 != 0;
            using var start = new ManualResetEventSlim(false);
            var update = Task.Run(() => { start.Wait(); return f.Engine.Update(source, sequence, Observe(A, value)); });
            var subscribe = Task.Run(() => { start.Wait(); return f.Engine.Subscribe([A]); });
            start.Set();
            await Task.WhenAll(update, subscribe);
            var result = await subscribe;
            using var sub = result.Subscription;
            var observed = result.Snapshot.States[A].Observation.Value!.Boolean;
            if (sub.TryRead(out var delivery))
            {
                Assert.True(delivery!.Revision > result.Snapshot.Revision);
                observed = delivery.State!.Observation.Value!.Boolean;
            }
            Assert.Equal(value, observed);
        }
    }

    [Fact]
    public void SnapshotDeliveryIsExplicitBaselineEvenIfValueUnchanged()
    {
        using var f = new Fixture();
        var source = Ready(f, A);
        using var sub = f.Engine.Subscribe([A]).Subscription;
        f.Engine.ApplySnapshot(source, 10, [Observe(A, false)]);
        Assert.True(sub.TryRead(out var delivery));
        Assert.Equal(DeliveryKind.SnapshotApplied, delivery!.Kind);
        Assert.False(delivery.State!.Observation.Value!.Boolean);
    }

    [Fact]
    public void ReplayOfOrderedInputsAndClockIsDeterministic()
    {
        static string[] Replay()
        {
            using var f = new Fixture();
            var source = Ready(f, A);
            f.Engine.SetDependencies("z", [A]);
            f.Engine.SetDependencies("a", [A]);
            using var sub = f.Engine.Subscribe([A]).Subscription;
            f.Engine.Update(source, 1, Observe(A, true));
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Engine.Update(source, 3, Observe(A, false));
            f.Engine.ApplySnapshot(source, 4, [Observe(A, false, f.Clock.UtcNow)]);
            var trace = new List<string>();
            while (sub.TryRead(out var d)) trace.Add($"{d!.Kind}|{d.Revision}|{d.State}|{string.Join(',', d.RuleIds)}");
            return trace.ToArray();
        }
        Assert.Equal(Replay(), Replay());
    }

    [Fact]
    public void OversizedDuplicateInputIsRejectedInsteadOfSilentlyTruncated()
    {
        using var f = new Fixture();
        Assert.Throws<ArgumentException>(() => f.Engine.RegisterSource("s", Enumerable.Repeat(A, 16_385)));
        Assert.Throws<ArgumentException>(() => f.Engine.Subscribe(Enumerable.Repeat(A, 16_385)));
        Assert.Throws<ArgumentException>(() => f.Engine.SetDependencies("r", Enumerable.Repeat(A, 1025)));
        Assert.Empty(f.Engine.ReadSnapshot().States);
    }
}
