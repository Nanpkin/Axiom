using Axiom.Domain;

namespace Axiom.Engine;

public sealed partial class EngineHost
{
    private sealed record Deadline(object Owner, TimeSpan At, long Order, Action Callback);
    private readonly SortedSet<Deadline> deadlines = new(Comparer<Deadline>.Create((a, b) =>
    {
        var comparison = a.At.CompareTo(b.At);
        return comparison != 0 ? comparison : a.Order.CompareTo(b.Order);
    }));
    private readonly Dictionary<object, Deadline> deadlinesByOwner = [];
    private IDisposable? timerLease;
    private TimeSpan? timerAt;
    private long timerVersion;
    private long deadlineOrder;
    private bool firingDeadlines;
    public string? RuntimeFault { get; private set; }
    public int PendingDeadlines { get { lock (gate) return deadlines.Count; } }

    private void SetDeadline(object owner, TimeSpan at, Action callback)
    {
        if (deadlinesByOwner.Remove(owner, out var old)) deadlines.Remove(old);
        var item = new Deadline(owner, at, ++deadlineOrder, callback);
        deadlinesByOwner.Add(owner, item);
        deadlines.Add(item);
        ArmTimer();
    }
    private void CancelDeadline(object owner)
    {
        if (!deadlinesByOwner.Remove(owner, out var item)) return;
        deadlines.Remove(item);
        ArmTimer();
    }
    private void ArmTimer()
    {
        if (firingDeadlines || !running) return;
        var nearest = deadlines.Min?.At;
        if (nearest == timerAt) return;
        timerVersion++;
        timerLease?.Dispose(); timerLease = null; timerAt = nearest;
        if (nearest is not { } at) return;
        var version = timerVersion;
        try { timerLease = Scheduler.Schedule(at, () =>
        {
            lock (gate)
            {
                if (!running || version != timerVersion) return;
                timerLease?.Dispose(); timerLease = null; timerAt = null;
                firingDeadlines = true;
                try
                {
                    while (deadlines.Min is { } item && item.At <= clock.MonotonicNow)
                    {
                        deadlines.Remove(item); deadlinesByOwner.Remove(item.Owner);
                        item.Callback();
                    }
                }
                finally { firingDeadlines = false; ArmTimer(); }
            }
        }); }
        catch (Exception exception)
        {
            RuntimeFault = "Deadline scheduling failed: " + exception.Message;
            Stop();
            throw new InvalidOperationException(RuntimeFault, exception);
        }
    }
    private void ScheduleFreshness(RuntimeRule rule)
    {
        var owner = ("freshness", rule.Definition.Id);
        if (!rule.Definition.Enabled || Paused(rule) || rule.Definition.Timing.MaximumInputAge is not { } age)
        { CancelDeadline(owner); return; }
        TimeSpan? earliest = null;
        foreach (var key in rule.Definition.Condition.Dependencies)
        {
            if (!receivedAt.TryGetValue(key, out var at)) continue;
            var due = at + age;
            if (due <= clock.MonotonicNow) continue;
            if (earliest is null || due < earliest) earliest = due;
        }
        if (earliest is { } deadline)
            SetDeadline(owner, deadline, () => { EvaluateRule(rule, false, fromDeadline: true); Recompute(rule.Definition.Action.Key); });
        else CancelDeadline(owner);
    }
    partial void ObservationRefreshed(ResourceKey key)
    {
        foreach (var id in index.Match(key))
        {
            if (!rules.TryGetValue(id, out var rule)) continue;
            if (rule.Reason == "InputStale") { EvaluateRule(rule, false); Recompute(rule.Definition.Action.Key); }
            else ScheduleFreshness(rule);
        }
    }
    partial void RemoveRuleState(RuntimeRule rule)
    {
        CancelDeadline(("rule", rule.Definition.Id));
        CancelDeadline(("freshness", rule.Definition.Id));
    }
    partial void OverrideChanged(ResourceKey key, TimeSpan? duration)
    {
        CancelDeadline(("override", key));
        if (duration is not { } remaining || !overrides.TryGetValue(key, out var item)) return;
        SetDeadline(("override", key), clock.MonotonicNow + remaining, () =>
        {
            if (overrides.GetValueOrDefault(key) != item) return;
            overrides.Remove(key); configurationRevision++;
            Recompute(key); DemandChanged();
        });
    }
    private void StopDeadlines()
    {
        timerVersion++; timerLease?.Dispose(); timerLease = null; timerAt = null;
        deadlines.Clear(); deadlinesByOwner.Clear();
    }
}
