using Axiom.Domain;
using Axiom.Infrastructure;
using static Axiom.Engine.Tests.AutomationFixture;
using static Axiom.Engine.Tests.RuntimeFixture;

namespace Axiom.Engine.Tests;

public sealed class ObservationReadinessReviewTests
{
    [Fact]
    public async Task NewTransitiveDependencyBaselineResumesAwaitingDesired()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        var acquiring = Barrier(); var acquire = Barrier();
        f.Observer.BeforeAcquire = key =>
        {
            if (key != Noise) return Task.CompletedTask;
            acquiring.TrySetResult(); return acquire.Task;
        };
        using var input = f.Engine.Subscribe([Input]).Subscription;
        try
        {
            f.Engine.RegisterCapability(Binding("output", dependencies: [Noise]));
            await Within(acquiring.Task);
            f.Observer.Emit(Input, true);
            Assert.True((await Within(input.ReadAsync().AsTask())).State!.Observation.Value!.Boolean);
            Assert.Equal(DecisionStatus.AwaitingObservation, f.Engine.GetDecision(Output).Status);
            Assert.Equal(0, f.Executor.Count);

            acquire.TrySetResult();
            var call = await Within(f.Executor.NextAsync());
            Assert.Equal(Output, call.Desired!.Key);
            Assert.Equal(StateQuality.Known, f.Engine.ReadSnapshot().States[Noise].Observation.Quality);
            Assert.Equal(1, f.Executor.Count);
            call.Complete(); await Within(f.Engine.WaitForIdleAsync());
        }
        finally { acquire.TrySetResult(); await f.Stop(); }
    }

    [Fact]
    public async Task CallbackAdmittedBeforeRecoveryCannotGapTheNewEpoch()
    {
        var f = new RuntimeFixture(); await Within(f.Start(command: true));
        Action<ObserverEvent> oldCallback;
        lock (f.Observer.Leases) oldCallback = f.Observer.Leases[Input].Callback;
        var admitted = Barrier(); using var continueCallback = new ManualResetEventSlim();
        f.Host.AfterCallbackAdmission = () =>
        {
            admitted.TrySetResult();
            Assert.True(continueCallback.Wait(TimeSpan.FromSeconds(10)));
        };
        var delayedCallback = Task.Run(() => oldCallback(new(0, null, true)));
        await Within(admitted.Task);
        f.Host.AfterCallbackAdmission = null;
        try
        {
            using (var recovery = f.Engine.Subscribe([Input]).Subscription)
            {
                f.Host.Recover();
                while ((await Within(recovery.ReadAsync().AsTask())).Kind != DeliveryKind.SnapshotApplied) { }
            }
            // A post-recovery edge proves the fresh leases and their baseline have become effective.
            f.Observer.Emit(Input, true);
            var call = await Within(f.Executor.NextAsync());
            call.Complete(); await Within(f.Engine.WaitForIdleAsync());
            using var changes = f.Engine.Subscribe([Input]).Subscription;
            continueCallback.Set(); await Within(delayedCallback);
            f.Observer.Emit(Input, false);
            var change = await Within(changes.ReadAsync().AsTask());
            Assert.Equal(StateQuality.Known, change.State!.Observation.Quality);
            Assert.False(change.State.Observation.Value!.Boolean);
            Assert.Null(f.Host.Error);
            Assert.Equal(1, f.Executor.Count);
        }
        finally
        {
            f.Host.AfterCallbackAdmission = null; continueCallback.Set();
            await Within(delayedCallback); await f.Stop();
        }
    }
}
