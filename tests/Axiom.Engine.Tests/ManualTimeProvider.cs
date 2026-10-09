namespace Axiom.Engine.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private long ticks;
    private DateTimeOffset utc = DateTimeOffset.UnixEpoch;
    internal readonly List<ManualTimer> Timers = [];
    internal bool FailChange;
    internal Action<TimeSpan>? TimerCreated;
    internal int FailNextChanges;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return ticks; }
    public override DateTimeOffset GetUtcNow() { lock (gate) return utc; }
    internal void JumpUtc(TimeSpan by) { lock (gate) utc += by; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate) Timers.Add(timer);
        timer.Change(dueTime, period);
        TimerCreated?.Invoke(dueTime);
        return timer;
    }
    internal void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (gate)
        {
            ticks += by.Ticks; utc += by;
            due = Timers.Where(t => t.At is { } at && at <= ticks).ToArray();
            foreach (var timer in due) timer.At = null;
        }
        foreach (var timer in due) timer.FireEvenIfDisposed();
    }
    internal sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal long? At;
        internal TimeSpan LastDue;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (owner.FailChange || owner.FailNextChanges-- > 0) throw new InvalidOperationException("timer fault");
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                if (disposed) return false;
                LastDue = dueTime;
                At = dueTime == Timeout.InfiniteTimeSpan ? null : checked(owner.ticks + dueTime.Ticks);
                return true;
            }
        }
        internal void FireEvenIfDisposed() => callback(state);
        public void Dispose() { lock (owner.gate) { disposed = true; At = null; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
