using Axiom.Domain;

namespace Axiom.Engine.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public void StartStopAreRepeatableAndIdleNeverSchedulesOrExecutes()
    {
        using var f = new Fixture();
        for (var i = 0; i < 20; i++)
        {
            f.Engine.Start();
            Assert.True(f.Engine.IsRunning);
            f.Scheduler.Advance(TimeSpan.FromDays(1));
            f.Engine.Stop();
            f.Engine.Stop();
            Assert.False(f.Engine.IsRunning);
        }
        Assert.Equal(0, f.Scheduler.Scheduled);
        Assert.Equal(0, f.Scheduler.Pending);
        Assert.Equal(0, f.Scheduler.Fired);
        Assert.Equal(0, f.Executor.Calls);
        f.Engine.Dispose();
        Assert.Throws<ObjectDisposedException>(f.Engine.Start);
    }

    [Fact]
    public void FakeTimeOnlyFiresDueWorkAndCancelledLeaseDoesNotFire()
    {
        var clock = new FakeClock();
        var scheduler = new FakeScheduler(clock);
        var called = 0;
        using var first = scheduler.Schedule(TimeSpan.FromSeconds(2), () => called++);
        scheduler.Schedule(TimeSpan.FromSeconds(1), () => called += 10).Dispose();
        scheduler.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, called);
        scheduler.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, called);
        Assert.Equal(0, scheduler.Pending);
    }

    [Fact]
    public async Task FakeExecutorPreservesUnknownAndCancellation()
    {
        var executor = new FakeExecutor();
        var command = new Command(Guid.NewGuid(), new("c", "r", ResourceScope.Machine),
            StateValue.From(new("bool", 1), true), "u", DateTimeOffset.UnixEpoch, false);
        var result = await executor.ExecuteCommandAsync(command, CancellationToken.None);
        Assert.Equal(OutcomeStatus.Unknown, result.Status);
        Assert.Equal(command.CommandId, result.CorrelationId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteCommandAsync(command, new CancellationToken(true)));
        Assert.Equal(1, executor.Calls);
    }
}
