using System.Threading.Channels;
using Axiom.Contracts;

namespace Axiom.SessionAgent;

internal sealed class WindowOnceSchedule
{
    private sealed class Entry(WindowOnceSpec spec)
    {
        internal readonly WindowOnceSpec Spec = spec;
        internal DateOnly? LastOffered;
        internal DateTimeOffset Next;
        internal decimal Count;
    }
    private readonly Entry[] entries;
    private TimeZoneInfo zone;
    internal WindowOnceSchedule(WindowOnceSpec[] specs, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (specs.Length is < 1 or > 128) throw new ArgumentException("时段检测资源数量无效。");
        entries = [.. specs.Distinct().Select(s => new Entry(s))]; this.zone = zone; Reset(now, zone);
    }
    internal void Reset(DateTimeOffset now, TimeZoneInfo currentZone)
    {
        zone = currentZone;
        foreach (var entry in entries) entry.Next = entry.Spec.NextBoundary(now, zone);
    }
    internal (WindowOnceSpec Spec, decimal Count)[] Due(DateTimeOffset now, bool clockChanged = false)
    {
        var ready = new List<(WindowOnceSpec, decimal)>();
        foreach (var entry in entries)
        {
            if (entry.Spec.WindowDate(now, zone) is { } date && (entry.LastOffered is null || date > entry.LastOffered.Value || clockChanged && date == entry.LastOffered.Value))
            { entry.LastOffered = date; ready.Add((entry.Spec, ++entry.Count)); }
            entry.Next = entry.Spec.NextBoundary(now, zone);
        }
        return [.. ready];
    }
    internal TimeSpan Remaining(DateTimeOffset now) => TimeSpan.FromTicks(Math.Min(TimeSpan.FromDays(49).Ticks,
        Math.Max(0, entries.Min(e => e.Next.UtcTicks) - now.UtcTicks)));
}

public static class WindowOnceEventSource
{
    public static async Task RunAsync(WindowOnceSpec[] specs, ChannelReader<byte> clockChanges, Action<WindowOnceSpec, decimal> publish,
        CancellationToken token, TimeProvider? timeProvider = null, Func<TimeZoneInfo>? readZone = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        readZone ??= () => { TimeZoneInfo.ClearCachedData(); return TimeZoneInfo.Local; };
        var zone = readZone(); var schedule = new WindowOnceSchedule(specs, time.GetUtcNow(), zone); var clockChanged = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            foreach (var (spec, count) in schedule.Due(time.GetUtcNow(), clockChanged)) { token.ThrowIfCancellationRequested(); publish(spec, count); }
            clockChanged = false;
            using var iteration = CancellationTokenSource.CreateLinkedTokenSource(token);
            var due = Task.Delay(schedule.Remaining(time.GetUtcNow()), time, iteration.Token);
            var changed = clockChanges.ReadAsync(iteration.Token).AsTask();
            try
            {
                await Task.WhenAny(due, changed); token.ThrowIfCancellationRequested();
                if (changed.IsCompleted) { await changed; clockChanges.TryRead(out _); clockChanged = true; }
                else await due;
                zone = readZone(); schedule.Reset(time.GetUtcNow(), zone);
            }
            finally
            {
                iteration.Cancel();
                try { await Task.WhenAll(due, changed); } catch (OperationCanceledException) when (iteration.IsCancellationRequested) { }
            }
        }
    }
}
