using Axiom.Domain;
using static Axiom.Engine.Tests.AutomationFixture;

namespace Axiom.Engine.Tests;

public sealed class ExecutionTests
{
    [Fact(Timeout = 10000)]
    public async Task ReceiptNeverFakesObservedSuccessAndFreshObservationConfirms()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        f.Engine.SetRule(f.Rule(priority: 42)); f.Engine.StartExecution();
        f.Engine.Update(f.Source, ++f.Sequence, new(Output, StateQuality.Unknown, null, f.Clock.UtcNow));
        f.Update(true);
        await f.Engine.WaitForIdleAsync();
        Assert.Equal(0, executor.Count);
        Assert.Equal(DecisionStatus.AwaitingObservation, f.Engine.GetDecision(Output).Status);
        f.Update(false, Output);
        var call = await executor.NextAsync();
        Assert.Equal(42, call.Desired!.Priority);
        call.Complete();
        await f.Engine.WaitForIdleAsync();
        Assert.Equal(DecisionStatus.AwaitingObservation, f.Engine.GetDecision(Output).Status);
        Assert.False(f.Engine.ReadSnapshot().States[Output].Observation.Value!.Boolean);
        f.Update(true, Output);
        Assert.Equal(DecisionStatus.Confirmed, f.Engine.GetDecision(Output).Status);
        Assert.Equal(0, f.Engine.PendingDeadlines);
    }

    [Fact(Timeout = 10000)]
    public async Task SameTargetRuleEditChangesGenerationAndOldReceiptNeedsNewObservation()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        f.Engine.RegisterCapability(Binding("output", cancellable: false));
        f.Engine.SetRule(f.Rule()); f.Engine.StartExecution(); f.Update(true);
        var first = await executor.NextAsync();
        var oldGeneration = first.Desired!.Generation;
        f.Engine.SetRule(f.Rule(version: 2));
        Assert.True(f.Engine.GetDecision(Output).Generation > oldGeneration);
        Assert.Equal(1, f.Engine.InFlightExecutions);
        first.Complete(); await f.Engine.WaitForIdleAsync();
        Assert.Equal(DecisionStatus.AwaitingObservation, f.Engine.GetDecision(Output).Status);
        f.Update(true, Output);
        Assert.Equal(DecisionStatus.Confirmed, f.Engine.GetDecision(Output).Status);
        Assert.Equal(1, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task CommandEdgesCooldownAndSnapshotNeverReplay()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        f.Update(true);
        f.Engine.SetRule(f.Rule(command: true, timing: new(cooldown: TimeSpan.FromSeconds(10))));
        f.Engine.StartExecution(); await f.Engine.WaitForIdleAsync();
        Assert.Equal(0, executor.Count);
        f.Update(false); f.Update(true);
        var first = await executor.NextAsync(); first.Complete(OutcomeStatus.Unknown);
        await f.Engine.WaitForIdleAsync();
        f.Update(false); f.Update(true); await f.Engine.WaitForIdleAsync();
        Assert.Equal(1, executor.Count);
        Assert.Contains("Cooldown", f.Engine.GetRuleEvaluation("rule").Reason);
        f.Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, executor.Count);
        f.Update(false); f.Update(true);
        var second = await executor.NextAsync();
        Assert.NotEqual(first.Command!.CommandId, second.Command!.CommandId);
        second.Complete(); await f.Engine.WaitForIdleAsync();
        f.Engine.ApplySnapshot(f.Source, ++f.Sequence, [Observe(Input, true), Observe(Output, false), Observe(Noise, false)]);
        await f.Engine.WaitForIdleAsync();
        Assert.Equal(2, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task TransientSettingsUseCappedFiniteBackoffThenWaitForExternalChange()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        f.Engine.SetRule(f.Rule()); f.Engine.StartExecution(new(maximumAttempts: 3)); f.Update(true);
        var first = await executor.NextAsync(); first.Complete(OutcomeStatus.Failed, FailureKind.Transient);
        await f.Engine.WaitForIdleAsync(); Assert.Equal(1, f.Scheduler.Pending);
        f.Scheduler.Advance(TimeSpan.FromSeconds(1));
        var second = await executor.NextAsync(); second.Complete(OutcomeStatus.Failed, FailureKind.Transient);
        await f.Engine.WaitForIdleAsync();
        f.Scheduler.Advance(TimeSpan.FromSeconds(2));
        var third = await executor.NextAsync(); third.Complete(OutcomeStatus.Failed, FailureKind.Transient);
        await f.Engine.WaitForIdleAsync();
        Assert.Equal(DecisionStatus.Blocked, f.Engine.GetDecision(Output).Status);
        Assert.Equal(0, f.Engine.PendingDeadlines);
        f.Scheduler.Advance(TimeSpan.FromDays(1)); Assert.Equal(3, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task NonCancellableTimeoutKeepsPhysicalReservationAndStopCanBeBoundedByCaller()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        f.Engine.RegisterCapability(Binding("output", cancellable: false));
        f.Engine.SetRule(f.Rule(command: true)); f.Engine.StartExecution(); f.Update(true);
        var call = await executor.NextAsync();
        f.Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(DecisionStatus.Unknown, f.Engine.GetDecision(Output).Status);
        Assert.Equal(1, f.Engine.InFlightExecutions);
        f.Update(false); f.Update(true);
        Assert.Equal(1, executor.Count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Engine.StopAndWaitAsync(new CancellationToken(true)));
        f.Engine.Start();
        Assert.Throws<InvalidOperationException>(() => f.Engine.ConfigureAutomation(f.Authorization, new SequentialIds()));
        call.Complete(OutcomeStatus.Unknown);
        await f.Engine.WaitForIdleAsync();
        Assert.Equal(0, f.Engine.InFlightExecutions);
    }

    [Fact(Timeout = 10000)]
    public async Task PendingReceiptRetainsReservationUntilCorrelatedFinalReceipt()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        f.Engine.SetRule(f.Rule()); f.Engine.StartExecution(); f.Update(true);
        var call = await executor.NextAsync();
        var receipt = f.Engine.WaitForExecutionReceiptAsync(Output);
        call.Complete(OutcomeStatus.Pending); await receipt;
        Assert.Equal(1, f.Engine.InFlightExecutions);
        Assert.False(f.Engine.ReportExecutionOutcome(Output, call.Desired!.Generation + 1, new(OutcomeStatus.Succeeded, "wrong", call.Id)));
        Assert.True(f.Engine.ReportExecutionOutcome(Output, call.Desired.Generation, new(OutcomeStatus.Succeeded, "final", call.Id)));
        await f.Engine.WaitForIdleAsync();
        Assert.Equal(DecisionStatus.AwaitingObservation, f.Engine.GetDecision(Output).Status);
        f.Update(true, Output);
        Assert.Equal(DecisionStatus.Confirmed, f.Engine.GetDecision(Output).Status);
    }

    [Fact(Timeout = 10000)]
    public async Task QueueIsBoundedAndPermissionRevocationBypassesQueuedData()
    {
        var executor = new ControlledExecutor();
        using var f = new AutomationFixture(executor);
        var outputs = Enumerable.Range(0, 6).Select(i => new ResourceKey("output", $"r{i}", ResourceScope.Machine)).ToArray();
        for (var i = 0; i < outputs.Length; i++) f.Engine.SetRule(f.Rule($"r{i}", command: true, output: outputs[i]));
        f.Engine.StartExecution(new(concurrency: 1, queueCapacity: 2)); f.Update(true);
        var first = await executor.NextAsync();
        Assert.Equal(1, f.Engine.InFlightExecutions);
        Assert.InRange(f.Engine.PendingExecutions, 0, 2);
        Assert.Equal(4, outputs.Count(key => f.Engine.GetDecision(key).Reason.Contains("CapacityExceeded")));
        f.Authorization.AllowWrite = false; f.Engine.RefreshAuthorization();
        Assert.Equal(0, f.Engine.PendingExecutions);
        Assert.True(first.Token.IsCancellationRequested);
        first.Complete(OutcomeStatus.Unknown); await f.Engine.WaitForIdleAsync();
        Assert.Equal(1, executor.Count);
    }
}
