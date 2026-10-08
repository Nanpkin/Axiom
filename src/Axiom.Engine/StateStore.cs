using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Engine;

public enum UpdateStatus { Accepted, Unchanged, StaleSequence, StaleEpoch, SnapshotRequired, Gap }
public sealed record UpdateResult(UpdateStatus Status, long Revision, int ChangedResources);
public sealed record StateSnapshot(long Revision, ImmutableDictionary<ResourceKey, ObservedState> States);
internal sealed record Mutation(UpdateResult Result, ImmutableArray<ObservedState> Changes, bool IsSnapshot = false,
    ImmutableArray<ObservedState> Observations = default)
{
    internal ImmutableArray<ObservedState> Touched => Observations.IsDefault ? Changes : Observations;
}

// Owned by EngineHost's gate. No independent writer or persistence side effects.
internal sealed class StateStore
{
    private sealed class Source(SourceSession session, HashSet<ResourceKey> keys)
    {
        internal SourceSession Session = session;
        internal HashSet<ResourceKey> Keys = keys;
        internal long Sequence = -1;
        internal bool NeedsSnapshot = true;
        internal bool Connected = true;
    }
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<ResourceKey, ObservedState> states = [];
    private long epoch;
    internal long Revision { get; private set; }
    internal ObservedState? Get(ResourceKey key) => states.GetValueOrDefault(key);

    internal (SourceSession Session, Mutation Mutation) Register(string id, IEnumerable<ResourceKey> resources, DateTimeOffset now)
    {
        Contract.Text(id, nameof(id));
        ArgumentNullException.ThrowIfNull(resources);
        var input = resources.Take(16_385).ToArray();
        if (input.Length > 16_384) throw new ArgumentException("Resource input exceeds capacity.", nameof(resources));
        var keys = input.ToHashSet();
        if (keys.Count == 0 || keys.Count > 16_384 || keys.Contains(null!)) throw new ArgumentException("Invalid resource set.", nameof(resources));
        if (sources.TryGetValue(id, out var previous))
        {
            if (!previous.Keys.SetEquals(keys)) throw new ArgumentException("Re-handshake must retain the declared resource set until the source is released.");
        }
        else
        {
            if (sources.Count >= 128 || states.Count + keys.Count > 16_384) throw new InvalidOperationException("Source/resource capacity exceeded.");
            if (keys.Any(states.ContainsKey)) throw new ArgumentException("Resource already has an owner.");
        }
        var session = new SourceSession(id, checked(++epoch));
        var source = new Source(session, keys);
        sources[id] = source;
        var changes = keys.Select(key => new Observation(key, StateQuality.Unknown, null, now)).ToArray();
        return (session, Commit(source, 0, changes, UpdateStatus.Accepted, force: true));
    }

    internal Mutation Snapshot(SourceSession session, long sequence, IReadOnlyCollection<Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observations);
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!TrySource(session, out var source)) return Reject(UpdateStatus.StaleEpoch);
        if (sequence <= source.Sequence) return Reject(UpdateStatus.StaleSequence);
        if (observations.Count != source.Keys.Count || observations.Any(x => x is null) ||
            !source.Keys.SetEquals(observations.Select(x => x.Key)) || observations.Select(x => x.Key).Distinct().Count() != observations.Count)
            throw new ArgumentException("Snapshot must contain each declared resource exactly once.", nameof(observations));
        source.Sequence = sequence;
        source.NeedsSnapshot = false;
        // A snapshot establishes a new baseline, never an uninterrupted edge history.
        return Commit(source, sequence, observations, UpdateStatus.Accepted, force: true) with { IsSnapshot = true };
    }

    internal Mutation Update(SourceSession session, long sequence, Observation observation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observation);
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!TrySource(session, out var source)) return Reject(UpdateStatus.StaleEpoch);
        if (!source.Keys.Contains(observation.Key)) throw new ArgumentException("Scope/resource does not belong to source.", nameof(observation));
        if (sequence <= source.Sequence) return Reject(UpdateStatus.StaleSequence);
        if (source.NeedsSnapshot) return Reject(UpdateStatus.SnapshotRequired);
        if (sequence - source.Sequence != 1)
        {
            source.Sequence = sequence;
            source.NeedsSnapshot = true;
            return Invalidate(source, now, UpdateStatus.Gap);
        }
        source.Sequence = sequence;
        return Commit(source, sequence, [observation], UpdateStatus.Accepted);
    }

    internal Mutation Disconnect(SourceSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!TrySource(session, out var source)) return Reject(UpdateStatus.StaleEpoch);
        source.Connected = false;
        source.NeedsSnapshot = true;
        return Invalidate(source, now, UpdateStatus.Gap);
    }

    internal Mutation Release(SourceSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!sources.TryGetValue(session.SourceId, out var source) || source.Session != session) return Reject(UpdateStatus.StaleEpoch);
        var mutation = Invalidate(source, now, UpdateStatus.Gap);
        sources.Remove(session.SourceId);
        foreach (var key in source.Keys) states.Remove(key);
        return mutation;
    }

    private Mutation Invalidate(Source source, DateTimeOffset now, UpdateStatus status) => Commit(source,
        Math.Max(0, source.Sequence), source.Keys.Select(key => new Observation(key, StateQuality.Unknown, null, now)).ToArray(), status, force: true);

    private bool TrySource(SourceSession session, out Source source)
    {
        if (sources.TryGetValue(session.SourceId, out var found) && found.Session == session && found.Connected)
        {
            source = found;
            return true;
        }
        source = null!;
        return false;
    }

    private Mutation Reject(UpdateStatus status) => new(new(status, Revision, 0), []);

    private Mutation Commit(Source source, long sequence, IReadOnlyCollection<Observation> observations, UpdateStatus status, bool force = false)
    {
        bool Changed(Observation observation) => force || !states.TryGetValue(observation.Key, out var old) ||
            old.Observation.Quality != observation.Quality || old.Observation.Value != observation.Value;
        var anyChanged = observations.Any(Changed);
        if (anyChanged) Revision = checked(Revision + 1);
        var changes = ImmutableArray.CreateBuilder<ObservedState>();
        var touched = ImmutableArray.CreateBuilder<ObservedState>();
        foreach (var observation in observations)
        {
            var changed = Changed(observation);
            var revision = changed ? Revision : states[observation.Key].Revision;
            var state = new ObservedState(observation, source.Session, sequence, revision);
            states[observation.Key] = state;
            touched.Add(state);
            if (changed) changes.Add(state);
        }
        return new(new(anyChanged || status == UpdateStatus.Gap ? status : UpdateStatus.Unchanged, Revision, changes.Count), changes.ToImmutable(), Observations: touched.ToImmutable());
    }

    internal StateSnapshot Read(IEnumerable<ResourceKey>? keys = null) => new(Revision,
        keys is null ? states.ToImmutableDictionary() : keys.Where(states.ContainsKey).ToImmutableDictionary(key => key, key => states[key]));

    internal void Clear()
    {
        sources.Clear();
        states.Clear();
        // Epoch and revision never regress across Start/Stop within this Engine instance.
    }
}
