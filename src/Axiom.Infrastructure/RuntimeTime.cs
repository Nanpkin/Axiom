using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class RuntimeClock(TimeProvider timeProvider) : IEngineClock
{
    public TimeProvider TimeProvider { get; } = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly long origin = timeProvider.GetTimestamp();
    public TimeZoneInfo LocalTimeZone => TimeProvider.LocalTimeZone;
    public DateTimeOffset UtcNow => TimeProvider.GetUtcNow();
    public TimeSpan MonotonicNow => TimeProvider.GetElapsedTime(origin);
}

/// <summary>One-shot timers. Cancellation never joins a callback that may be waiting for Engine's gate.</summary>
public sealed class DeadlineScheduler(RuntimeClock clock) : IDeadlineScheduler, IDisposable
{
    private readonly RuntimeClock clock = clock;
    public static readonly TimeSpan MaximumSlice = TimeSpan.FromMilliseconds(4_294_967_294);
    private readonly object gate = new();
    private readonly HashSet<Lease> leases = [];
    private bool disposed;
    private TaskCompletionSource<Exception> fault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Exception> Fault { get { lock (gate) return fault.Task; } }
    public int ActiveTimers { get { lock (gate) return leases.Count; } }

    internal Task<Exception> BeginLifecycle()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (leases.Count != 0) throw new InvalidOperationException("Previous lifecycle still owns timers.");
            fault = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return fault.Task;
        }
    }

    public IDisposable Schedule(TimeSpan deadline, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var lease = new Lease(this, deadline, callback, fault);
            leases.Add(lease);
            try { lease.Arm(); return lease; }
            catch (Exception exception) { lease.Dispose(); fault.TrySetResult(exception); throw; }
        }
    }

    private sealed class Lease(DeadlineScheduler owner, TimeSpan deadline, Action callback, TaskCompletionSource<Exception> failure) : IDisposable
    {
        private ITimer? timer;
        private bool cancelled;
        internal void Arm()
        {
            var now = owner.clock.MonotonicNow;
            var remaining = deadline <= now ? TimeSpan.Zero : deadline - now;
            var due = remaining > MaximumSlice ? MaximumSlice : remaining;
            // System timers truncate to milliseconds. Round positive waits up so an early
            // callback cannot create a zero-delay rearm loop for a sub-millisecond remainder.
            if (due > TimeSpan.Zero)
                due = TimeSpan.FromTicks(((due.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond);
            timer ??= owner.clock.TimeProvider.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (!timer.Change(due, Timeout.InfiniteTimeSpan)) throw new InvalidOperationException("Timer could not be armed.");
        }
        private void Fire()
        {
            try
            {
                lock (owner.gate)
                {
                    if (cancelled) return;
                    if (owner.clock.MonotonicNow < deadline) { Arm(); return; }
                    Dispose();
                }
                callback(); // Never invoke Engine under the scheduler lock.
            }
            catch (Exception exception)
            {
                Dispose();
                failure.TrySetResult(exception);
            }
        }
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (cancelled) return;
                cancelled = true;
                timer?.Dispose(); timer = null;
                owner.leases.Remove(this);
            }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var lease in leases.ToArray()) lease.Dispose();
        }
    }
}
