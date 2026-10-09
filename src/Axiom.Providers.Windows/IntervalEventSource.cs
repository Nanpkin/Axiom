namespace Axiom.Providers.Windows;

// Deadlines, not environment polling. One pending timer serves all distinct active intervals.
internal sealed class IntervalSchedule
{
    private sealed class Entry(int minutes) { internal readonly int Minutes = minutes; internal TimeSpan Next = TimeSpan.FromMinutes(minutes); internal decimal Count; }
    private readonly Entry[] entries;
    internal IntervalSchedule(int[] minutes)
    {
        if (minutes.Length is < 1 or > 128 || minutes.Any(m => m is < 1 or > 1440)) throw new ArgumentException("计时范围无效。");
        entries = [.. minutes.Distinct().Order().Select(m => new Entry(m))];
    }
    internal TimeSpan Remaining(TimeSpan elapsed) => TimeSpan.FromTicks(Math.Max(0, entries.Min(e => e.Next.Ticks) - elapsed.Ticks));
    internal (int Minutes, decimal Count)[] Due(TimeSpan elapsed)
    {
        var ready = new List<(int, decimal)>();
        foreach (var entry in entries)
        {
            if (entry.Next > elapsed) continue;
            entry.Next = elapsed + TimeSpan.FromMinutes(entry.Minutes);
            ready.Add((entry.Minutes, ++entry.Count));
        }
        return [.. ready];
    }
}
public static class IntervalEventSource
{
    public static async Task RunAsync(int[] minutes, Action<int, decimal> publish, CancellationToken token, TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System; var schedule = new IntervalSchedule(minutes); var started = time.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(schedule.Remaining(time.GetElapsedTime(started)), time, token);
            token.ThrowIfCancellationRequested();
            foreach (var (interval, count) in schedule.Due(time.GetElapsedTime(started)))
            {
                token.ThrowIfCancellationRequested(); publish(interval, count);
            }
        }
    }
}
