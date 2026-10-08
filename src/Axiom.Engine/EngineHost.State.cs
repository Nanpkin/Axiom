using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Engine;

public sealed record SnapshotSubscription(StateSnapshot Snapshot, StateSubscription Subscription);

public sealed partial class EngineHost
{
    private readonly StateStore store = new();
    private readonly EventBus bus = new();
    private readonly DependencyIndex index = new();
    public int ActiveSubscriptions { get { lock (gate) return bus.Count; } }

    public SourceSession RegisterSource(string sourceId, IEnumerable<ResourceKey> resources)
    {
        lock (gate)
        {
            RequireRunning();
            var result = store.Register(sourceId, resources, clock.UtcNow);
            Publish(result.Mutation);
            return result.Session;
        }
    }
    public UpdateResult ApplySnapshot(SourceSession source, long sequence, IReadOnlyCollection<Observation> observations)
    {
        lock (gate) { RequireRunning(); return Publish(store.Snapshot(source, sequence, observations)); }
    }
    public UpdateResult Update(SourceSession source, long sequence, Observation observation)
    {
        lock (gate) { RequireRunning(); return Publish(store.Update(source, sequence, observation, clock.UtcNow)); }
    }
    public UpdateResult Disconnect(SourceSession source)
    {
        lock (gate) { RequireRunning(); return Publish(store.Disconnect(source, clock.UtcNow)); }
    }
    public UpdateResult ReleaseSource(SourceSession source)
    {
        lock (gate)
        {
            RequireRunning();
            var mutation = store.Release(source, clock.UtcNow);
            var result = Publish(mutation);
            foreach (var state in mutation.Touched)
            { receivedAt.Remove(state.Observation.Key); observationVersions.Remove(state.Observation.Key); }
            return result;
        }
    }
    public StateSnapshot ReadSnapshot()
    {
        lock (gate) { RequireRunning(); return store.Read(); }
    }
    public SnapshotSubscription Subscribe(IEnumerable<ResourceKey> resources, int capacity = 256, EventFidelity fidelity = EventFidelity.EdgeSensitive)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (capacity is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(capacity));
        Contract.EnumValue(fidelity);
        var input = resources.Take(16_385).ToArray();
        if (input.Length > 16_384) throw new ArgumentException("Subscription input exceeds capacity.", nameof(resources));
        var keys = input.ToImmutableHashSet();
        if (keys.Count == 0 || keys.Count > 16_384 || keys.Contains(null!)) throw new ArgumentException("Invalid subscription resources.");
        lock (gate)
        {
            RequireRunning();
            var subscription = bus.Subscribe(keys, capacity, fidelity, Release);
            return new(store.Read(keys), subscription);
        }
    }
    private void Release(StateSubscription subscription) { lock (gate) bus.Remove(subscription); }
    public void SetDependencies(string ruleId, IEnumerable<ResourceKey> dependencies)
    {
        lock (gate) { RequireRunning(); if (rules.ContainsKey(ruleId)) throw new InvalidOperationException("Use SetRule for compiled rules."); index.Set(ruleId, dependencies); }
    }
    public void RemoveDependencies(string ruleId)
    {
        Contract.Text(ruleId, nameof(ruleId));
        lock (gate) { RequireRunning(); if (rules.ContainsKey(ruleId)) throw new InvalidOperationException("Use RemoveRule for compiled rules."); index.Remove(ruleId); }
    }
    public ImmutableArray<string> MatchDependencies(ResourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (gate) { RequireRunning(); return index.Match(key); }
    }
    private UpdateResult Publish(Mutation mutation)
    {
        OnMutation(mutation);
        foreach (var state in mutation.Changes)
            bus.Publish(new(mutation.IsSnapshot ? DeliveryKind.SnapshotApplied : DeliveryKind.Changed,
                state.Revision, state, index.Match(state.Observation.Key), null));
        return mutation.Result;
    }
    partial void StopCore()
    {
        ResetDisplayObservation();
        StopAutomation();
        bus.Stop(store.Revision);
        store.Clear();
        index.Clear();
    }
}
