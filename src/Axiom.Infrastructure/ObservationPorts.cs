using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed record ObserverEvent(long Sequence, Observation? Observation, bool Gap = false);
public sealed record ObserverSnapshot(long Sequence, IReadOnlyCollection<Observation> Observations);

public interface IObservationLease
{
    ObservationRequirement Guarantee { get; }
    // Failure leaves ownership with the caller; retry only on an explicit lifecycle signal.
    ValueTask ReleaseAsync();
}

public interface IRuntimeObserver
{
    string SourceId { get; }
    IReadOnlyCollection<ResourceKey> Resources { get; }
    // Must either return ownership or clean up before throwing. Callback is bounded/nonblocking.
    ValueTask<IObservationLease> AcquireAsync(ResourceKey key, Action<ObserverEvent> callback);
    // Consistent source sequence watermark; inactive resources are not sampled.
    ValueTask<ObserverSnapshot> SnapshotAsync(IReadOnlyCollection<ResourceKey> activeResources);
}

public sealed record RuntimeStopResult(bool Completed, IReadOnlyCollection<ResourceKey> Held, int InFlight, string? Error);
