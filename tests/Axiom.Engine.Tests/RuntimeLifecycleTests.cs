using Axiom.Domain;
using static Axiom.Engine.Tests.AutomationFixture;
using static Axiom.Engine.Tests.RuntimeFixture;

namespace Axiom.Engine.Tests;

public sealed class RuntimeLifecycleTests
{
    [Fact]
    public async Task SharedDemandBootstrapsOnlyActiveResourcesAndStopsWithoutTimers()
    {
        var f = new RuntimeFixture();
        await Within(f.Start());
        Assert.Equal(2, f.Host.Held.Length);
        Assert.Equal(StateQuality.Unknown, f.Engine.ReadSnapshot().States[Noise].Observation.Quality);
        f.Engine.SetRule(f.Rule("shared"));
        f.Engine.RemoveRule("rule");
        Assert.Equal(2, f.Observer.History.Count);
        Assert.Equal(0, f.Scheduler.ActiveTimers);
        await f.Stop(); await f.Stop();
        await Within(f.Start());
        await f.Stop();
    }

    [Fact]
    public async Task EventDuringSnapshotIsRetainedAfterWatermark()
    {
        var f = new RuntimeFixture();
        var snapshot = Barrier();
        f.Observer.BeforeSnapshot = () => snapshot.Task;
        var start = f.Start(command: true);
        await Within(f.Observer.Snapshots.Reader.ReadAsync().AsTask());
        f.Observer.Emit(Input, true);
        snapshot.SetResult();
        await Within(start);
        var call = await Within(f.Executor.NextAsync());
        Assert.NotNull(call.Command);
        call.Complete();
        await Within(f.Engine.WaitForIdleAsync());
        Assert.Equal(1, f.Executor.Count);
        await f.Stop();
    }

    [Fact]
    public async Task StartupFailureReleasesAcquiredResources()
    {
        var f = new RuntimeFixture();
        var calls = 0;
        f.Observer.BeforeAcquire = _ => ++calls == 2 ? Task.FromException(new InvalidOperationException("acquire failed")) : Task.CompletedTask;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Within(f.Start()));
        await Within(f.Host.Cleanup);
        Assert.Empty(f.Host.Held);
        Assert.Single(f.Observer.History);
        Assert.False(f.Engine.IsRunning);
        Assert.Equal(0, f.Scheduler.ActiveTimers);
    }

    [Fact]
    public async Task WeakGuaranteeCannotStartExecution()
    {
        var f = new RuntimeFixture(); f.Observer.Weak = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Within(f.Start()));
        await Within(f.Host.Cleanup);
        Assert.Empty(f.Host.Held);
        Assert.Equal(0, f.Executor.Count);
    }

    [Fact]
    public async Task StopDuringAcquireTracksLateLeaseAndRejectsRestart()
    {
        var f = new RuntimeFixture();
        var entered = Barrier(); var acquire = Barrier();
        f.Observer.BeforeAcquire = _ => { entered.TrySetResult(); return acquire.Task; };
        var start = f.Start();
        await Within(entered.Task);
        var stop = f.Host.StopAsync(TimeSpan.FromSeconds(2));
        f.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.False((await Within(stop)).Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Start());
        acquire.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(start));
        await Within(f.Host.Cleanup);
        Assert.Empty(f.Host.Held);
        await f.Stop();
    }

    [Fact]
    public async Task StopTimeoutKeepsPhysicalFlightAndLateCompletionReleasesDependencies()
    {
        var f = new RuntimeFixture(); await Within(f.Start(cancellable: false));
        f.Observer.Emit(Input, true);
        var call = await Within(f.Executor.NextAsync());
        var stop = f.Host.StopAsync(TimeSpan.FromSeconds(2));
        f.Time.Advance(TimeSpan.FromSeconds(2));
        var result = await Within(stop);
        Assert.False(result.Completed); Assert.Equal(1, result.InFlight); Assert.Equal(2, result.Held.Count);
        Assert.False(call.Token.IsCancellationRequested);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Start());
        call.Complete();
        await Within(f.Host.Cleanup);
        await f.Stop();
        Assert.Equal(1, f.Executor.Count);
    }

    [Fact]
    public async Task ReleaseFailureWaitsForExplicitRetryAndReportsResidual()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        var releaseFailed = Barrier(); var attempts = 0;
        f.Observer.BeforeRelease = _ => { attempts++; releaseFailed.TrySetResult(); return Task.FromException(new InvalidOperationException("release failed")); };
        var stop = f.Host.StopAsync(TimeSpan.FromSeconds(2));
        await Within(releaseFailed.Task);
        f.Time.Advance(TimeSpan.FromSeconds(2));
        var result = await Within(stop);
        Assert.False(result.Completed); Assert.NotEmpty(result.Held);
        Assert.Equal(1, attempts);
        f.Observer.BeforeRelease = null;
        await f.Stop();
    }

    [Fact]
    public async Task GapAndReconnectRejectOldCallbacksAndNeverReplayCommandBaseline()
    {
        var f = new RuntimeFixture(); await Within(f.Start(command: true));
        using var updates = f.Engine.Subscribe([Input]).Subscription;
        var old = f.Observer.Leases[Input];
        f.Observer.Gap();
        var invalid = await Within(updates.ReadAsync().AsTask());
        Assert.Equal(StateQuality.Unknown, invalid.State!.Observation.Quality);
        f.Observer.Values[Input] = true;
        f.Host.Recover();
        StateDelivery baseline;
        do { baseline = await Within(updates.ReadAsync().AsTask()); } while (baseline.Kind != DeliveryKind.SnapshotApplied);
        Assert.True(baseline.State!.Observation.Value!.Boolean);
        Assert.Equal(0, f.Executor.Count);
        old.Callback(new(999, Observe(Input, false)));
        f.Observer.Emit(Input, false);
        f.Observer.Emit(Input, true);
        var call = await Within(f.Executor.NextAsync()); call.Complete();
        await Within(f.Engine.WaitForIdleAsync());
        Assert.Equal(1, f.Executor.Count);
        await f.Stop();
    }

    [Fact]
    public async Task OverflowWhileSnapshotPendingInvalidatesBaselineAndCleansUp()
    {
        var f = new RuntimeFixture(capacity: 1);
        var snapshot = Barrier(); f.Observer.BeforeSnapshot = () => snapshot.Task;
        var start = f.Start(command: true);
        await Within(f.Observer.Snapshots.Reader.ReadAsync().AsTask());
        f.Observer.Emit(Input, true); f.Observer.Emit(Input, false);
        snapshot.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Within(start));
        await Within(f.Host.Cleanup);
        Assert.Equal(0, f.Executor.Count); Assert.Empty(f.Host.Held);
    }

    [Fact]
    public async Task DesiredReceiptRequiresIndependentObservationThroughHost()
    {
        var f = new RuntimeFixture(); f.Observer.Values[Input] = true;
        await Within(f.Start());
        var call = await Within(f.Executor.NextAsync()); call.Complete();
        await Within(f.Engine.WaitForIdleAsync());
        Assert.False(f.Engine.ReadSnapshot().States[Output].Observation.Value!.Boolean);
        Assert.NotEqual(DecisionStatus.Confirmed, f.Engine.GetDecision(Output).Status);
        using var output = f.Engine.Subscribe([Output]).Subscription;
        f.Observer.Emit(Output, true);
        await Within(output.ReadAsync().AsTask());
        Assert.Equal(DecisionStatus.Confirmed, f.Engine.GetDecision(Output).Status);
        Assert.Equal(1, f.Executor.Count);
        await f.Stop();
    }
}
