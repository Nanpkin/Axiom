using Axiom.Domain;

namespace Axiom.Engine.Tests;

internal sealed class FakeClock : IEngineClock
{
    public TimeZoneInfo LocalTimeZone { get; set; } = TimeZoneInfo.Utc;
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
    public TimeSpan MonotonicNow { get; private set; }
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(by));
        MonotonicNow += by;
        UtcNow += by;
    }
}

internal sealed class FakeScheduler(FakeClock clock) : IDeadlineScheduler
{
    private readonly List<Entry> pending = [];
    public int Scheduled { get; private set; }
    public int Fired { get; private set; }
    public int Pending => pending.Count;
    public IDisposable Schedule(TimeSpan deadline, Action callback)
    {
        Scheduled++;
        var entry = new Entry(deadline, callback, pending);
        pending.Add(entry);
        return entry;
    }
    public void Advance(TimeSpan by)
    {
        clock.Advance(by);
        foreach (var entry in pending.Where(x => x.Deadline <= clock.MonotonicNow).OrderBy(x => x.Deadline).ToArray())
        {
            entry.Dispose();
            Fired++;
            entry.Callback();
        }
    }
    private sealed class Entry(TimeSpan deadline, Action callback, List<Entry> owner) : IDisposable
    {
        public TimeSpan Deadline { get; } = deadline;
        public Action Callback { get; } = callback;
        public void Dispose() => owner.Remove(this);
    }
}

internal sealed class FakeExecutor : ICapabilityExecutor
{
    public int Calls { get; private set; }
    public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return ValueTask.FromResult(new Outcome(OutcomeStatus.Unknown, "fake timeout; no automatic retry", command.CommandId));
    }
    public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return ValueTask.FromResult(new Outcome(OutcomeStatus.Pending, "await independent observation", Guid.NewGuid()));
    }
}

internal sealed class Fixture : IDisposable
{
    public FakeClock Clock { get; } = new();
    public FakeScheduler Scheduler { get; }
    public FakeExecutor Executor { get; } = new();
    public EngineHost Engine { get; }
    public Fixture()
    {
        Scheduler = new(Clock);
        Engine = new(Clock, Scheduler, Executor);
        Engine.Start();
    }
    public void Dispose() => Engine.Dispose();
}
