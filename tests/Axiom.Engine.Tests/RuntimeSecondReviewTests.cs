using System.Reflection;
using Axiom.Domain;
using Axiom.Infrastructure;
using static Axiom.Engine.Tests.AutomationFixture;
using static Axiom.Engine.Tests.RuntimeFixture;

namespace Axiom.Engine.Tests;

public sealed class RuntimeSecondReviewTests
{
    private static object Gate(object obj) => obj.GetType().GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static long LastSequence(RuntimeFixture f) => (long)typeof(ControlledRuntimeHost).GetField("lastSequence", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Host)!;
    private static RuleDefinition NoiseRule(long version = 1) => new("noise", version, "owner", ResourceScope.Machine,
        RuleCondition.Equal(Noise, StateValue.From(Schema, true)), RuleAction.Desired(Output, StateValue.From(Schema, false)));
    private static Task IngressThrough(RuntimeFixture f, long sequence)
    {
        var reached = Barrier();
        f.Host.AfterIngressBatch = () => { if (LastSequence(f) >= sequence) reached.TrySetResult(); return Task.CompletedTask; };
        return reached.Task;
    }

    [Fact]
    public async Task S1_DeferredAndLiveNotificationsKeepResourceOrder()
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        var captured = Barrier(); var snapshot = Barrier(); var nextPlan = Barrier(); var plan = Barrier();
        var drained = Barrier();
        f.Host.AfterDeferredBatch = _ => { drained.TrySetResult(); return Task.CompletedTask; };
        f.Observer.BeforeSnapshot = () => { captured.TrySetResult(); return snapshot.Task; };
        f.Engine.SetRule(NoiseRule()); await Within(captured.Task);
        using var changes = f.Engine.Subscribe([Noise]).Subscription;
        var firstBuffered = IngressThrough(f, f.Observer.Sequence + 1);
        f.Observer.Emit(Noise, true); await Within(firstBuffered);
        f.Host.BeforePlanIteration = () => { nextPlan.TrySetResult(); return plan.Task; };
        f.Engine.SetRule(NoiseRule(2));
        try
        {
            snapshot.SetResult(); await Within(nextPlan.Task);
            Assert.Equal(DeliveryKind.SnapshotApplied, (await Within(changes.ReadAsync().AsTask())).Kind);
            var secondConsumed = IngressThrough(f, f.Observer.Sequence + 1);
            f.Observer.Emit(Noise, false); await Within(secondConsumed);
            f.Host.BeforePlanIteration = null; plan.SetResult();
            await Within(drained.Task);
            Assert.False(f.Engine.ReadSnapshot().States[Noise].Observation.Value!.Boolean);
            var first = await Within(changes.ReadAsync().AsTask());
            var second = await Within(changes.ReadAsync().AsTask());
            Assert.True(first.State!.Observation.Value!.Boolean);
            Assert.False(second.State!.Observation.Value!.Boolean);
            Assert.False(f.Engine.ReadSnapshot().States[Noise].Observation.Value!.Boolean);
            Assert.Equal(0, f.Executor.Count);
        }
        finally { f.Host.BeforePlanIteration = null; snapshot.TrySetResult(); plan.TrySetResult(); await f.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S2_CommandDequeueRearmFailureCleansActualResources(bool fail)
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        // Holding Engine gate makes enqueue/fault injection atomic with respect to the worker.
        lock (Gate(f.Engine))
        {
            f.Engine.TakeOverride(Input, "owner", "later", TimeSpan.FromSeconds(30));
            var accepted = f.Engine.SubmitManualCommand(new(new Guid(9010, 0, 0, new byte[8]), Output,
                StateValue.From(Schema, true), "owner", f.Clock.UtcNow + TimeSpan.FromSeconds(10), false));
            Assert.Equal(OutcomeStatus.Accepted, accepted.Status);
            Assert.Equal(1, f.Engine.PendingExecutions);
            Assert.Equal(0, f.Executor.Count);
            if (fail) f.Time.FailNextChanges = 1;
        }
        if (fail)
        {
            await Within(f.Host.Cleanup);
            Assert.Equal(0, f.Executor.Count);
            Assert.Equal(0, f.Engine.PendingExecutions + f.Engine.InFlightExecutions);
            Assert.Empty(f.Host.Held); Assert.Empty(f.Observer.Leases);
            Assert.Contains("timer fault", f.Host.Error);
            Assert.Contains("timer fault", f.Engine.RuntimeFault);
            Assert.Equal(0, f.Scheduler.ActiveTimers);
        }
        else
        {
            var call = await Within(f.Executor.NextAsync()); Assert.NotNull(call.Command);
            call.Complete(); await Within(f.Engine.WaitForIdleAsync());
            Assert.Equal(1, f.Executor.Count); Assert.Null(f.Host.Error); await f.Stop();
        }
    }

    [Fact]
    public async Task S3_ConfigurationRecoveryIsolatesAlreadyAdmittedOldGap()
    {
        var f = new RuntimeFixture(); await Within(f.Start(command: true));
        var old = f.Observer.Leases[Input].Callback;
        var admitted = Barrier(); using var release = new ManualResetEventSlim();
        f.Host.AfterCallbackAdmission = () => { admitted.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var late = Task.Run(() => old(new(0, null, true)));
        await Within(admitted.Task); f.Host.AfterCallbackAdmission = null;
        try
        {
            using var recovery = f.Engine.Subscribe([Input]).Subscription;
            f.Observer.Gap();
            Assert.Equal(StateQuality.Unknown, (await Within(recovery.ReadAsync().AsTask())).State!.Observation.Quality);
            f.Engine.SetRule(f.Rule(command: true, version: 2));
            while ((await Within(recovery.ReadAsync().AsTask())).Kind != DeliveryKind.SnapshotApplied) { }
            f.Observer.Emit(Input, true);
            var call = await Within(f.Executor.NextAsync()); call.Complete(); await Within(f.Engine.WaitForIdleAsync());
            using var fresh = f.Engine.Subscribe([Input]).Subscription;
            release.Set(); await Within(late);
            f.Observer.Emit(Input, false);
            var change = await Within(fresh.ReadAsync().AsTask());
            Assert.Equal(StateQuality.Known, change.State!.Observation.Quality);
            Assert.False(change.State.Observation.Value!.Boolean);
            Assert.Null(f.Host.Error); Assert.Equal(1, f.Executor.Count);
        }
        finally { release.Set(); await Within(late); await f.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S4_DeferredBatchesYieldGateAfter32AndPreserveOrder(bool stopAtBoundary)
    {
        var f = new RuntimeFixture(); await Within(f.Start());
        var captured = Barrier(); var snapshot = Barrier(); var batch = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = Barrier();
        f.Observer.BeforeSnapshot = () => { captured.TrySetResult(); return snapshot.Task; };
        f.Engine.SetRule(NoiseRule()); await Within(captured.Task);
        using var changes = f.Engine.Subscribe([Noise]).Subscription;
        var buffered = IngressThrough(f, f.Observer.Sequence + 33);
        for (var i = 0; i < 33; i++) f.Observer.Emit(Noise, i % 2 == 0);
        await Within(buffered);
        f.Host.AfterDeferredBatch = count => { batch.TrySetResult(count); return resume.Task; };
        try
        {
            snapshot.SetResult();
            Assert.Equal(32, await Within(batch.Task));
            // This control-side read must acquire gate while the consumer is between batches.
            await Within(Task.Run(() => { lock (Gate(f.Host)) Assert.Equal(3, f.Host.Held.Length); }));
            Assert.Equal(DeliveryKind.SnapshotApplied, (await Within(changes.ReadAsync().AsTask())).Kind);
            for (var i = 0; i < 32; i++) Assert.Equal(i % 2 == 0, (await Within(changes.ReadAsync().AsTask())).State!.Observation.Value!.Boolean);
            Assert.False(f.Engine.ReadSnapshot().States[Noise].Observation.Value!.Boolean);
            if (stopAtBoundary)
            {
                var stop = f.Host.StopAsync(TimeSpan.FromSeconds(5));
                Assert.False(f.Engine.IsRunning); // Stop has acquired gate before the next data batch.
                resume.SetResult();
                Assert.True((await Within(stop)).Completed);
                Assert.Empty(f.Host.Held); Assert.Empty(f.Observer.Leases);
                Assert.Equal(0, f.Executor.Count);
                return;
            }
            f.Host.AfterDeferredBatch = null; resume.SetResult();
            Assert.True((await Within(changes.ReadAsync().AsTask())).State!.Observation.Value!.Boolean);
            Assert.True(f.Engine.ReadSnapshot().States[Noise].Observation.Value!.Boolean);
            Assert.Equal(0, f.Executor.Count);
        }
        finally { f.Host.AfterDeferredBatch = null; snapshot.TrySetResult(); resume.TrySetResult(); await f.Stop(); }
    }
}
