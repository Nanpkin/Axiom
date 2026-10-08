namespace Axiom.Engine.Tests;

public sealed class StoppedObservationTests
{
    [Fact]
    public async Task StoppedPlanConsumerCanWaitUntilCancelledWithoutSpinning()
    {
        using var f = new AutomationFixture();
        f.Engine.Stop();
        var plan = f.Engine.GetObservationPlan();
        using var cancellation = new CancellationTokenSource();
        var waiting = f.Engine.WaitForObservationPlanAsync(plan.Version, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
