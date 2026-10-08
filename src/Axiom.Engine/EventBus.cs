using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Engine;

public enum DeliveryKind { Changed, SnapshotApplied, SnapshotRequired }
public sealed record StateDelivery(DeliveryKind Kind, long Revision, ObservedState? State, ImmutableArray<string> RuleIds, string? Reason);

public sealed class StateSubscription : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<StateDelivery> queue = new();
    private TaskCompletionSource signal = NewSignal();
    private readonly Action<StateSubscription> release;
    private bool terminated;
    private bool disposed;
    public int Capacity { get; }
    public EventFidelity Fidelity { get; }
    internal ImmutableHashSet<ResourceKey> Keys { get; }
    private int highWatermark;
    private long dropped;
    public int HighWatermark { get { lock (gate) return highWatermark; } }
    public long Dropped { get { lock (gate) return dropped; } }
    public int PendingCount { get { lock (gate) return queue.Count; } }
    public bool NeedsSnapshot { get { lock (gate) return terminated; } }

    internal StateSubscription(ImmutableHashSet<ResourceKey> keys, int capacity, EventFidelity fidelity, Action<StateSubscription> release)
    {
        Keys = keys;
        Capacity = capacity;
        Fidelity = fidelity;
        this.release = release;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Wake() { var previous = signal; signal = NewSignal(); previous.TrySetResult(); }

    // False asks EventBus to detach immediately. Gap occupies one reserved terminal slot.
    internal bool Publish(StateDelivery delivery)
    {
        lock (gate)
        {
            if (terminated || disposed) return false;
            if (queue.Count == Capacity)
            {
                dropped += queue.Count + 1;
                queue.Clear();
                terminated = true;
                queue.Enqueue(new(DeliveryKind.SnapshotRequired, delivery.Revision, null, [], "CapacityExceeded"));
                Wake();
                return false;
            }
            queue.Enqueue(delivery);
            highWatermark = Math.Max(highWatermark, queue.Count);
            Wake();
            return true;
        }
    }

    internal void Finish(long revision, string reason)
    {
        lock (gate)
        {
            if (disposed || terminated) return;
            dropped += queue.Count;
            queue.Clear();
            terminated = true;
            queue.Enqueue(new(DeliveryKind.SnapshotRequired, revision, null, [], reason));
            Wake();
        }
    }

    public bool TryRead(out StateDelivery? delivery)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return queue.TryDequeue(out delivery);
        }
    }

    public async ValueTask<StateDelivery> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                if (queue.TryDequeue(out var delivery)) return delivery;
                if (terminated) throw new InvalidOperationException("Subscription ended; establish a new atomic snapshot/subscription.");
                wait = signal.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            queue.Clear();
            Wake();
        }
        // Never call back into the Engine while holding the subscription gate.
        release(this);
    }
}

internal sealed class EventBus
{
    private readonly HashSet<StateSubscription> subscriptions = [];
    private readonly Dictionary<ResourceKey, HashSet<StateSubscription>> byKey = [];
    internal int Count => subscriptions.Count;
    internal StateSubscription Subscribe(ImmutableHashSet<ResourceKey> keys, int capacity, EventFidelity fidelity, Action<StateSubscription> release)
    {
        if (subscriptions.Count >= 128) throw new InvalidOperationException("Subscription capacity exceeded.");
        var subscription = new StateSubscription(keys, capacity, fidelity, release);
        subscriptions.Add(subscription);
        foreach (var key in keys)
        {
            if (!byKey.TryGetValue(key, out var set)) byKey[key] = set = [];
            set.Add(subscription);
        }
        return subscription;
    }
    internal void Publish(StateDelivery delivery)
    {
        if (!byKey.TryGetValue(delivery.State!.Observation.Key, out var set)) return;
        foreach (var subscription in set.ToArray())
            if (!subscription.Publish(delivery)) Remove(subscription);
    }
    internal void Remove(StateSubscription subscription)
    {
        if (!subscriptions.Remove(subscription)) return;
        foreach (var key in subscription.Keys)
        {
            var set = byKey[key];
            set.Remove(subscription);
            if (set.Count == 0) byKey.Remove(key);
        }
    }
    internal void Stop(long revision)
    {
        foreach (var subscription in subscriptions) subscription.Finish(revision, "EngineStopped");
        subscriptions.Clear();
        byKey.Clear();
    }
}
