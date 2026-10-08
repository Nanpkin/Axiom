using Axiom.Domain;

namespace Axiom.Engine;

public sealed record DisplayObservation(ObservedState? State, DateTimeOffset? LastSuccessAt,
    string? Error, bool Observing, int DemandCount, long Revision);

public sealed class DisplayDemandLease(Action release) : IDisposable
{
    private Action? release = release;
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

public sealed partial class EngineHost
{
    private ResourceKey? displayKey;
    private SourceSession? displaySource;
    private long displaySequence;
    private long displayRevision;
    private int displayDemand;
    private DateTimeOffset? displayLastSuccess;
    private string? displayError;
    private TaskCompletionSource displaySignal = NewSignal();

    // Separate latest-value display path: no OS watermark or finite freshness guarantee is claimed.
    public void ConfigureDisplayObservation(ResourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Scope.Kind != ScopeKind.Machine) throw new ArgumentException("Display service must be machine scoped.");
        lock (gate)
        {
            RequireRunning();
            if (displayKey is not null) throw new InvalidOperationException("Display resource already configured.");
            displayKey = key;
            displaySource = RegisterSource("display-service-status", [key]);
            ApplySnapshot(displaySource, 0, [new Observation(key, StateQuality.Unknown, null, clock.UtcNow)]);
            ChangedDisplay();
        }
    }

    public DisplayDemandLease AcquireDisplayObservation(ResourceKey key)
    {
        lock (gate)
        {
            RequireRunning();
            if (displayKey != key) throw new UnauthorizedAccessException("Display resource is not allowed.");
            if (displayDemand >= 8) throw new InvalidOperationException("Display demand capacity exceeded.");
            displayDemand++;
            ChangedDisplay();
            return new DisplayDemandLease(() => ReleaseDisplayObservation(key));
        }
    }

    private void ReleaseDisplayObservation(ResourceKey key)
    {
        lock (gate)
        {
            if (displayKey != key || displayDemand == 0) return;
            displayDemand--;
            if (displayDemand == 0)
            {
                displayError = null;
                if (displaySource is not null && running)
                    Update(displaySource, ++displaySequence, new Observation(key, StateQuality.Unknown, null, clock.UtcNow));
            }
            ChangedDisplay();
        }
    }

    public int DisplayDemandCount { get { lock (gate) return displayDemand; } }

    public DisplayObservation ReadDisplayObservation()
    {
        lock (gate)
        {
            RequireRunning();
            if (displayKey is null) throw new InvalidOperationException("Display resource not configured.");
            var state = store.Read([displayKey]).States.GetValueOrDefault(displayKey);
            return new(state, displayLastSuccess, displayError, displayDemand > 0, displayDemand, displayRevision);
        }
    }

    public async Task<long> WaitForDisplayChangeAsync(long afterRevision, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (displayRevision > afterRevision) return displayRevision;
                wait = displaySignal.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void PublishDisplaySample(ResourceKey key, StateQuality quality, StateValue? value, string? error = null)
    {
        lock (gate)
        {
            RequireRunning();
            if (displayKey != key || displaySource is null || displayDemand == 0) return;
            var now = clock.UtcNow;
            if (quality == StateQuality.Known) { displayLastSuccess = now; displayError = null; }
            else displayError = error ?? "Service status unavailable.";
            Update(displaySource, ++displaySequence, new Observation(key, quality, value, now));
            ChangedDisplay();
        }
    }

    private void ChangedDisplay()
    {
        displayRevision++;
        var previous = displaySignal;
        displaySignal = NewSignal();
        previous.TrySetResult();
    }

    private void ResetDisplayObservation()
    {
        displayDemand = 0;
        displayKey = null;
        displaySource = null;
        displaySequence = 0;
        displayLastSuccess = null;
        displayError = null;
        ChangedDisplay();
    }
}
