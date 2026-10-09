using System.Threading.Channels;
using Axiom.Contracts;

namespace Axiom.SessionAgent;

internal sealed class OnceSchedule
{
    private sealed class Entry(OnceTimeSpec spec) { internal readonly OnceTimeSpec Spec = spec; internal DateTimeOffset? At; internal bool Finished; internal decimal Count; internal string Reason = "等待指定日期和时刻"; }
    private readonly Entry[] entries;
    internal OnceSchedule(OnceTimeSpec[] specs, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (specs.Length is < 1 or > 128) throw new ArgumentException("单次定时资源数量无效。");
        entries = [.. specs.Distinct().Select(s => new Entry(s))]; Reset(now, zone);
    }
    internal (OnceTimeSpec Spec, decimal Count, string Reason)[] Snapshot => [.. entries.Select(e => (e.Spec, e.Count, e.Reason))];
    internal void Reset(DateTimeOffset now, TimeZoneInfo zone)
    {
        foreach (var entry in entries.Where(e => !e.Finished))
        {
            entry.At = entry.Spec.At(zone);
            if (entry.At is null || entry.At <= now)
            {
                entry.Finished = true;
                entry.Reason = entry.At is null ? "此时区不存在该时刻，已跳过；请修改日期或时间" : "指定时刻已过期，未补执行；可暂停或删除";
            }
        }
    }
    internal TimeSpan Remaining(DateTimeOffset now)
    {
        var next = entries.Where(e => !e.Finished).Min(e => e.At);
        // System timers accept at most ~49.7 days. Long calendar deadlines use bounded 49-day chunks.
        return next is null ? Timeout.InfiniteTimeSpan : TimeSpan.FromTicks(Math.Clamp(next.Value.UtcTicks - now.UtcTicks, 0, TimeSpan.FromDays(49).Ticks));
    }
    internal void Due(DateTimeOffset now)
    {
        foreach (var entry in entries.Where(e => !e.Finished && e.At <= now))
        {
            entry.Finished = true;
            if (now - entry.At <= TimeSpan.FromMinutes(1)) { entry.Count = 1; entry.Reason = "已到期并交给规则判断；动作结果请查看活动"; }
            else entry.Reason = "已错过指定时刻，未补执行；可暂停或删除";
        }
    }
}
public static class OnceEventSource
{
    public static async Task RunAsync(OnceTimeSpec[] specs, ChannelReader<byte> clockChanges,
        Action<bool, (OnceTimeSpec Spec, decimal Count, string Reason)[]> publish, CancellationToken token,
        TimeProvider? timeProvider = null, Func<TimeZoneInfo>? readZone = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        readZone ??= () => { TimeZoneInfo.ClearCachedData(); return TimeZoneInfo.Local; };
        var zone = readZone(); var schedule = new OnceSchedule(specs, time.GetUtcNow(), zone);
        publish(true, schedule.Snapshot);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var iteration = CancellationTokenSource.CreateLinkedTokenSource(token);
            var due = Task.Delay(schedule.Remaining(time.GetUtcNow()), time, iteration.Token);
            var changed = clockChanges.ReadAsync(iteration.Token).AsTask();
            try
            {
                await Task.WhenAny(due, changed); token.ThrowIfCancellationRequested();
                var before = schedule.Snapshot;
                if (changed.IsCompleted)
                {
                    await changed; clockChanges.TryRead(out _);
                    zone = readZone(); schedule.Reset(time.GetUtcNow(), zone);
                }
                else
                {
                    await due; var currentZone = readZone();
                    if (currentZone.Id != zone.Id || !currentZone.HasSameRules(zone)) { zone = currentZone; schedule.Reset(time.GetUtcNow(), zone); }
                    else schedule.Due(time.GetUtcNow());
                }
                var updates = schedule.Snapshot.Except(before).ToArray();
                if (updates.Length > 0) publish(false, updates);
            }
            finally
            {
                iteration.Cancel();
                try { await Task.WhenAll(due, changed); } catch (OperationCanceledException) when (iteration.IsCancellationRequested) { }
            }
        }
    }
}
