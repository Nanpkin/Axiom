using Axiom.Domain;

namespace Axiom.Engine;

public sealed partial class EngineHost
{
    private bool observationSafety;
    private readonly Dictionary<ResourceKey, ObservationRequirement> readyObservations = [];

    // The controlled host opts into physical lease admission before configuring automation.
    public void EnableObservationSafety()
    {
        lock (gate)
        {
            RequireRunning();
            if (authorization is not null) throw new InvalidOperationException("Enable before configuring automation.");
            observationSafety = true; readyObservations.Clear();
        }
    }

    public bool TryBeginObservationRelease(ResourceKey key, bool recovery = false)
    {
        lock (gate)
        {
            if (flights.Values.Any(f => (f.Work.RequiredResources.IsDefault ? [f.Work.Key] : f.Work.RequiredResources).Contains(key))) return false;
            if (running && !recovery && GetObservationPlan().References.ContainsKey(key)) return false;
            readyObservations.Remove(key);
            foreach (var work in pendingWork.ToArray())
                if ((work.RequiredResources.IsDefault ? [work.Key] : work.RequiredResources).Contains(key)) RemovePending(work.Key);
            SignalWork();
            return true;
        }
    }

    public bool TryApplyObservationSnapshot(long expectedPlanVersion, SourceSession source, long sequence,
        Observation observation, IReadOnlyDictionary<ResourceKey, ObservationRequirement> guarantees)
    {
        lock (gate)
        {
            if (!running || GetObservationPlan() is not { } plan || plan.Version != expectedPlanVersion ||
                !plan.References.ContainsKey(observation.Key) || plan.Requirements.Any(pair =>
                    !guarantees.TryGetValue(pair.Key, out var actual) || !Meets(actual, pair.Value))) return false;
            var mutation = store.Snapshot(source, sequence, [observation]);
            if (mutation.Result.Status != UpdateStatus.Accepted) return false;
            readyObservations[observation.Key] = guarantees[observation.Key];
            Publish(mutation);
            // A transitive capability dependency is not in the condition index. Its new
            // lease can unblock an existing Desired even when no condition value changed.
            foreach (var target in targetRules.Keys.ToArray())
                if (ExpandResources(targetRules[target].SelectMany(id => rules[id].Definition.Condition.Dependencies).Append(target)).Contains(observation.Key))
                    Recompute(target);
            return true;
        }
    }

    public bool TryDisconnect(SourceSession source)
    {
        lock (gate)
        {
            if (!running) return false;
            var mutation = store.Disconnect(source, clock.UtcNow);
            foreach (var state in mutation.Touched) readyObservations.Remove(state.Observation.Key);
            Publish(mutation);
            return true;
        }
    }

    private static bool Meets(ObservationRequirement actual, ObservationRequirement required) =>
        actual.MaximumDelay >= TimeSpan.Zero && actual.MaximumDelay <= required.MaximumDelay && (!required.PreserveEdges || actual.PreserveEdges);

    private bool ObservationInputsReady(IEnumerable<ResourceKey> roots)
    {
        if (!observationSafety) return true;
        GetBaseReferences(requestedMode); // Includes the current configuration even before its demand is published.
        return ExpandResources(roots).All(key => readyObservations.TryGetValue(key, out var actual) &&
            Meets(actual, baseRequirements.GetValueOrDefault(key) ?? new ObservationRequirement(
                capabilities.GetValueOrDefault(key.CapabilityId)?.Descriptor.MaximumRefreshDelay ?? TimeSpan.Zero, true)));
    }
}
