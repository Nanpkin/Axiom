using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class HardwareBrightnessBurstTests
{
    [Fact]
    public void HardwareBurstUsesNativeWriteCadenceAndConfirmsWithinOneSecondAndAtEndpoint()
    {
        var time = new ManualTimeProvider(); uint current = 0;
        var reads = new List<TimeSpan>(); var writes = new List<(TimeSpan At, uint Value)>();
        var target = new BrightnessRestoreTarget(0, p => (uint)p * 10, () =>
        {
            reads.Add(time.GetElapsedTime(0)); time.Advance(TimeSpan.FromMilliseconds(260)); return current;
        }, value =>
        {
            var now = time.GetElapsedTime(0);
            Assert.True(now - reads[^1] < TimeSpan.FromSeconds(1)); // No new Set after the segment deadline.
            writes.Add((now, value)); current = value; time.Advance(TimeSpan.FromMilliseconds(55)); return true;
        }, hardwareBurst: true);
        BrightnessFade.Apply([(target, 1000)], new(true, 3), default, time, (delay, _) =>
        { Assert.True(delay <= TimeSpan.FromMilliseconds(16)); time.Advance(delay); });
        Assert.True(target.Transaction.Owned); Assert.False(target.Transaction.HasPendingFadeWrite);
        Assert.Equal(1000u, target.Transaction.Confirmed); Assert.Equal(writes[^1].Value, current);
        Assert.True(reads[^1] > writes[^1].At); Assert.InRange(reads.Count, 3, 5);
        Assert.InRange(writes.Count, 30, 50); Assert.Equal(writes.Select(w => w.Value).Distinct(), writes.Select(w => w.Value));
        Assert.Equal(writes.Select(w => w.Value).Order(), writes.Select(w => w.Value));
        Assert.Contains(writes.Zip(writes.Skip(1)), pair => pair.Second.At - pair.First.At == TimeSpan.FromMilliseconds(55));
        Assert.All(writes.Zip(writes.Skip(1)), pair => Assert.True(pair.Second.At - pair.First.At >= TimeSpan.FromMilliseconds(55)));
        Assert.True(time.GetElapsedTime(0) < TimeSpan.FromMilliseconds(3500));
    }

    [Fact]
    public void BurstTracksOnlyLatestPendingValueAndCannotCompleteRestoreOrApplyUsingOldBaseline()
    {
        foreach (var operation in new[] { "confirm", "release", "apply" })
        {
            uint current = 100; var reads = 0; var writes = new List<uint>();
            var transaction = new ConditionalSettingTransaction<uint>(100, () => { reads++; return current; }, value => { writes.Add(value); current = value; }, "synthetic");
            Assert.True(transaction.AdvanceFade(90, default));
            Assert.True(transaction.AdvanceFade(80, default, continueBurst: true));
            Assert.True(transaction.AdvanceFade(80, default, continueBurst: true)); // Identical native values are never resent.
            Assert.Equal(1, reads); Assert.Equal(new uint[] { 90, 80 }, writes);
            Assert.Equal(100u, transaction.Confirmed); Assert.Equal(80u, transaction.FadeExpected);
            Assert.True(transaction.HasPendingFadeWrite); Assert.False(transaction.TryCompleteConfirmedRelease());
            if (operation == "confirm")
            {
                current = 90; // An earlier accepted value cannot confirm the newest pending value.
                Assert.False(transaction.ConfirmFade(default)); Assert.Equal(2, reads);
            }
            else
            {
                Assert.False((operation == "release" ? transaction.Release(default) : transaction.Apply(70, default)).Success);
                Assert.Equal(1, reads);
            }
            Assert.False(transaction.Owned); Assert.True(transaction.UnconfirmedFailure);
            Assert.Equal(new uint[] { 90, 80 }, writes);
        }
    }

    [Fact]
    public void HardwareBurstStopsOnRejectedWriteCancellationUnknownOrBoundaryMismatchWithoutRetry()
    {
        foreach (var failure in new[] { "write", "cancel", "unknown", "external" })
        {
            var time = new ManualTimeProvider(); uint current = 1000; var reads = 0; var writes = new List<uint>();
            var calls = new List<string>(); using var stop = new CancellationTokenSource();
            var target = new BrightnessRestoreTarget(1000, p => (uint)p * 10, () =>
            {
                calls.Add("read"); reads++;
                if (reads > 1 && failure == "unknown") return null;
                if (reads > 1 && failure == "external") current = 1000;
                return current;
            }, value =>
            {
                calls.Add("write"); writes.Add(value);
                if (writes.Count == 2 && failure == "write") return false;
                current = value;
                if (writes.Count == 2 && failure == "cancel") stop.Cancel();
                return true;
            }, hardwareBurst: true);
            if (failure == "cancel")
                Assert.ThrowsAny<OperationCanceledException>(() => BrightnessFade.Apply([(target, 0)], new(true, 3), stop.Token, time, (delay, _) => time.Advance(delay)));
            else BrightnessFade.Apply([(target, 0)], new(true, 3), stop.Token, time, (delay, _) => time.Advance(delay));
            Assert.False(target.Transaction.Owned); Assert.True(target.Transaction.UnconfirmedFailure);
            var beforeRelease = writes.Count; target.Transaction.Release(default); Assert.Equal(beforeRelease, writes.Count);
            if (failure is "write" or "cancel") { Assert.Equal(2, writes.Count); Assert.Equal(1, reads); Assert.Equal("write", calls[^1]); }
            else { Assert.True(writes.Count > 2); Assert.Equal(2, reads); Assert.Equal("read", calls[^1]); }
            if (failure == "external") Assert.Equal(1000u, current);
        }
    }

    [Fact]
    public void LateFramesConfirmExpiredBurstBeforeAnyFurtherWriteAndAlwaysConfirmFinalValue()
    {
        var time = new ManualTimeProvider(); uint current = 0; var calls = new List<string>();
        var target = new BrightnessRestoreTarget(0, p => (uint)p * 10,
            () => { calls.Add("read:" + current); return current; }, value => { calls.Add("write:" + value); current = value; return true; }, hardwareBurst: true);
        BrightnessFade.Apply([(target, 1000)], new(true, 3), default, time, (_, _) => time.Advance(TimeSpan.FromMilliseconds(1500)));
        Assert.Equal(new[] { "read:0", "write:500", "read:500", "write:1000", "read:1000" }, calls);
        Assert.Equal(1000u, target.Transaction.Confirmed); Assert.False(target.Transaction.HasPendingFadeWrite);
    }
}
