using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;

namespace Axiom.ServiceHost;

internal sealed partial class PluginTaskRuntime
{
    private const int WindowStampStride = 1024;
    private readonly TimeProvider windowTime;
    private WindowWatch? windowWatch;
    private readonly HashSet<Task> windowRetryWorkers = [];
    private sealed class WindowWatch(SourceSession source, CancellationToken token)
    {
        internal readonly SourceSession Source = source;
        internal readonly CancellationToken Token = token;
        internal long Sequence;
        internal bool Active = true;
        internal readonly Dictionary<ResourceKey, decimal> Counters = [];
        internal readonly HashSet<ResourceKey> ProviderKnown = [];
        internal readonly Dictionary<string, CancellationTokenSource> Retries = [];
    }
    private sealed record WindowAttempt(string TaskId, long Version, ResourceKey Source, DateOnly Date, Guid AttemptId, WindowOnceSpec Spec);

    private WindowWatch? BeginWindowWatch(SourceSession? source, ResourceKey[] keys, CancellationToken token)
    {
        if (source is null) return null;
        var current = new WindowWatch(source, token);
        lock (statusGate)
        {
            foreach (var key in keys.Where(k => k.CapabilityId == WindowOnceCapabilities.Due)) current.Counters.Add(key, 0);
            windowWatch = current;
        }
        return current;
    }
    private void EndWindowWatch(WindowWatch? current)
    {
        if (current is null) return;
        lock (statusGate)
        {
            current.Active = false;
            foreach (var retry in current.Retries.Values) retry.Cancel();
            current.Retries.Clear();
            if (ReferenceEquals(windowWatch, current)) windowWatch = null;
        }
    }
    private Observation[] WindowSnapshot(WindowWatch current, Observation[] observations)
    {
        foreach (var observation in observations.Where(o => o.Key.CapabilityId == WindowOnceCapabilities.Due))
            SetWindowProviderQuality(current, observation.Key, observation.Quality == StateQuality.Known);
        return [.. observations.Select(o => o.Key.CapabilityId == WindowOnceCapabilities.Due && o.Quality == StateQuality.Known
            ? new Observation(o.Key, o.Quality, StateValue.From(new("axiom.event-count", 1), current.Counters[o.Key]), o.ObservedAt) : o)];
    }
    private void SetWindowProviderQuality(WindowWatch current, ResourceKey key, bool known)
    {
        if (known) { current.ProviderKnown.Add(key); return; }
        current.ProviderKnown.Remove(key);
        var taskId = WindowOnceCapabilities.ParseResource(key.ResourceId).TaskId;
        if (current.Retries.Remove(taskId, out var retry)) retry.Cancel();
    }

    // Called under statusGate. Wire sequence is checked separately; locally scheduled retry
    // opportunities share this source's independently increasing Engine sequence.
    private void OfferWindowOpportunity(WindowWatch current, ResourceKey key)
    {
        if (!current.Active || current.Token.IsCancellationRequested || !connected || !current.ProviderKnown.Contains(key) || !current.Counters.TryGetValue(key, out var previous)) return;
        // The Agent and Host have separate timezone caches. Refresh only on an actual
        // calendar/clock opportunity, while deterministic injected clocks retain their zone.
        if (ReferenceEquals(windowTime, TimeProvider.System)) TimeZoneInfo.ClearCachedData();
        var parsed = WindowOnceCapabilities.ParseResource(key.ResourceId);
        var task = tasks.SingleOrDefault(t => t.Id == parsed.TaskId);
        if (task is null || task.Paused || task.Detection.CapabilityId != WindowOnceCapabilities.Due || WindowOnceCapabilities.Validate(task.Detection) != parsed.Spec) return;
        if (parsed.Spec.WindowDate(windowTime.GetUtcNow(), windowTime.LocalTimeZone) is not { } date) return;
        var floor = (decimal)date.DayNumber * WindowStampStride;
        if (previous >= floor + WindowStampStride - 1) return;
        var counter = Math.Max(floor + 1, previous + 1);
        current.Counters[key] = counter;
        observationReasons[key] = "已检测到当前时段，正在按任务条件判断。";
        engine.Update(current.Source, current.Sequence++, new(key, StateQuality.Known, StateValue.From(new("axiom.event-count", 1), counter), clock.UtcNow));
    }
    private async Task<bool> BeginWindowAttemptsAsync(Command command, List<WindowAttempt> attempts, CancellationToken token)
    {
        var allowed = command.Origins.IsEmpty; // Explicit manual commands have no rule origins.
        foreach (var origin in command.Origins)
        {
            AutomationTask? task;
            lock (statusGate) task = tasks.SingleOrDefault(t => t.Id == origin.RuleId && t.Version == origin.RuleVersion && !t.Paused);
            if (task is null) continue;
            if (origin.EventSource?.CapabilityId != WindowOnceCapabilities.Due) { allowed = true; continue; }
            var source = origin.EventSource;
            var parsed = WindowOnceCapabilities.ParseResource(source.ResourceId);
            if (parsed.TaskId != task.Id || task.Detection.CapabilityId != WindowOnceCapabilities.Due || parsed.Spec != WindowOnceCapabilities.Validate(task.Detection)) continue;
            if (origin.EventStamp?.Number is not { } stamp || stamp <= 0 || stamp != decimal.Truncate(stamp)) continue;
            var day = decimal.ToInt32(decimal.Floor(stamp / WindowStampStride));
            if (day is < 0 or > 3652058 || stamp % WindowStampStride == 0) continue;
            var date = DateOnly.FromDayNumber(day);
            if (parsed.Spec.WindowDate(windowTime.GetUtcNow(), windowTime.LocalTimeZone) != date) continue;
            var attempt = new WindowAttempt(task.Id, task.Version, source, date, command.CommandId, parsed.Spec);
            if (await database.BeginWindowOnceAttemptAsync(task.Id, task.Version, date, command.CommandId, windowTime.GetUtcNow(), token))
            { attempts.Add(attempt); allowed = true; }
            else
            {
                var state = await database.ReadWindowOnceAttemptAsync(task.Id, date, token);
                lock (statusGate)
                {
                    observationReasons[source] = WindowAttemptReason(state, parsed.Spec, date);
                    if (state is { Status: "failed", RetryAt: { } retryAt } && state.Attempts <= parsed.Spec.MaxRetries)
                        ScheduleWindowRetry(attempt, retryAt);
                }
            }
        }
        return allowed;
    }
    private string WindowAttemptReason(WindowOnceAttemptState? state, WindowOnceSpec spec, DateOnly date) => state?.Status switch
    {
        "succeeded" => "本时段动作已成功完成，不会重复执行。",
        "failed" when state.Attempts <= spec.MaxRetries && state.RetryAt is { } retryAt && spec.WindowDate(retryAt, windowTime.LocalTimeZone) != date
            => "本时段剩余时间不足以重试，等待下个选定时段。",
        "failed" when state.Attempts <= spec.MaxRetries => $"动作未完成，将间隔{spec.RetryMinutes}分钟重试；已尝试{state.Attempts}次。",
        "failed" => $"本时段重试次数已用完，共尝试{state.Attempts}次。",
        "started" or "unknown" => "本时段上次执行结果未确认，为避免重复操作，不自动重试。",
        _ => "等待当前任务生效或下一个选定时段。"
    };
    private async Task CompleteWindowAttemptsAsync(List<WindowAttempt> attempts, OutcomeStatus outcome)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var attempt in attempts)
        {
            await database.CompleteWindowOnceAttemptAsync(attempt.TaskId, attempt.Date, attempt.AttemptId, outcome,
                windowTime.GetUtcNow(), attempt.Spec.RetryMinutes, cleanup.Token);
            var state = await database.ReadWindowOnceAttemptAsync(attempt.TaskId, attempt.Date, cleanup.Token);
            lock (statusGate)
            {
                observationReasons[attempt.Source] = WindowAttemptReason(state, attempt.Spec, attempt.Date);
                if (outcome is OutcomeStatus.Failed or OutcomeStatus.Rejected && state is { RetryAt: { } retryAt })
                {
                    var task = tasks.SingleOrDefault(t => t.Id == attempt.TaskId && !t.Paused && t.Detection.CapabilityId == WindowOnceCapabilities.Due);
                    if (task is not null)
                    {
                        var spec = WindowOnceCapabilities.Validate(task.Detection);
                        if (state.Attempts <= spec.MaxRetries)
                            ScheduleWindowRetry(attempt with { Version = task.Version, Spec = spec,
                                Source = new(WindowOnceCapabilities.Due, WindowOnceCapabilities.Resource(task.Id, spec), scope) }, retryAt);
                    }
                }
            }
        }
    }
    private async Task RefreshWindowTaskAsync(AutomationTask task, CancellationToken token, bool existingAttemptOnly = false)
    {
        if (task.Paused || task.Detection.CapabilityId != WindowOnceCapabilities.Due) return;
        var spec = WindowOnceCapabilities.Validate(task.Detection);
        if (spec.WindowDate(windowTime.GetUtcNow(), windowTime.LocalTimeZone) is not { } date) return;
        var key = new ResourceKey(WindowOnceCapabilities.Due, WindowOnceCapabilities.Resource(task.Id, spec), scope);
        var state = await database.ReadWindowOnceAttemptAsync(task.Id, date, token);
        lock (statusGate)
        {
            var current = windowWatch;
            if (current is null || !current.Active || !connected || !current.ProviderKnown.Contains(key) || !current.Counters.ContainsKey(key) ||
                !tasks.Any(t => t.Id == task.Id && t.Version == task.Version && !t.Paused)) return;
            observationReasons[key] = WindowAttemptReason(state, spec, date);
            if (state is null && !existingAttemptOnly) OfferWindowOpportunity(current, key);
            else if (state is { Status: "failed", RetryAt: { } retryAt } && state.Attempts <= spec.MaxRetries)
            {
                if (retryAt <= windowTime.GetUtcNow()) OfferWindowOpportunity(current, key);
                else ScheduleWindowRetry(new(task.Id, task.Version, key, date, Guid.Empty, spec), retryAt);
            }
        }
    }
    private async Task RefreshKnownWindowTasksAsync(WindowWatch? current, CancellationToken token)
    {
        if (current is null) return;
        AutomationTask[] known;
        lock (statusGate) known = [.. tasks.Where(t => !t.Paused && t.Detection.CapabilityId == WindowOnceCapabilities.Due &&
            current.ProviderKnown.Contains(new(WindowOnceCapabilities.Due, WindowOnceCapabilities.Resource(t.Id, WindowOnceCapabilities.Validate(t.Detection)), scope)))];
        foreach (var task in known) await RefreshWindowTaskAsync(task, token, existingAttemptOnly: true);
    }
    private void ValidateWindowAttemptsBeforeSend(List<WindowAttempt> attempts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (statusGate)
            foreach (var attempt in attempts)
                if (!tasks.Any(t => t.Id == attempt.TaskId && t.Version == attempt.Version && !t.Paused) ||
                    windowWatch is not { Active: true } current || !connected || !current.ProviderKnown.Contains(attempt.Source) ||
                    attempt.Spec.WindowDate(windowTime.GetUtcNow(), windowTime.LocalTimeZone) != attempt.Date)
                    throw new OperationCanceledException("原时段已结束或任务已变更，尚未发送动作。", token);
    }
    // Only an explicit unsuccessful receipt creates a deadline. Succeeded/Unknown/startup
    // records never create periodic work; reconnect re-reads the same durable deadline.
    private void ScheduleWindowRetry(WindowAttempt attempt, DateTimeOffset retryAt)
    {
        var current = windowWatch;
        if (current is null || !current.Active || current.Token.IsCancellationRequested || !current.ProviderKnown.Contains(attempt.Source) || !current.Counters.ContainsKey(attempt.Source) ||
            !tasks.Any(t => t.Id == attempt.TaskId && t.Version == attempt.Version && !t.Paused) ||
            attempt.Spec.WindowDate(retryAt, windowTime.LocalTimeZone) != attempt.Date) return;
        if (current.Retries.Remove(attempt.TaskId, out var old)) old.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(current.Token);
        current.Retries.Add(attempt.TaskId, cancellation);
        windowRetryWorkers.RemoveWhere(t => t.IsCompleted);
        windowRetryWorkers.Add(RunWindowRetryAsync(current, attempt, retryAt, cancellation));
    }
    private async Task RunWindowRetryAsync(WindowWatch current, WindowAttempt attempt, DateTimeOffset retryAt, CancellationTokenSource cancellation)
    {
        // Always yield: a due reconnect must not synchronously reenter Engine before the
        // previous command receipt has released its physical reservation.
        await Task.Yield();
        try
        {
            while (true)
            {
                var delay = retryAt - windowTime.GetUtcNow();
                if (delay > TimeSpan.Zero) await Task.Delay(delay, windowTime, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var state = await database.ReadWindowOnceAttemptAsync(attempt.TaskId, attempt.Date, cancellation.Token);
                lock (statusGate)
                {
                    if (stopping || !current.Active || !connected || !ReferenceEquals(windowWatch, current) ||
                        !current.Retries.TryGetValue(attempt.TaskId, out var registered) || !ReferenceEquals(registered, cancellation) ||
                        !tasks.Any(t => t.Id == attempt.TaskId && t.Version == attempt.Version && !t.Paused) ||
                        attempt.Spec.WindowDate(windowTime.GetUtcNow(), windowTime.LocalTimeZone) != attempt.Date ||
                        state is not { Status: "failed", RetryAt: { } due } || state.Attempts > attempt.Spec.MaxRetries) return;
                    // A wall-clock rollback changes the remaining deadline, never a polling interval.
                    if (due > windowTime.GetUtcNow()) { retryAt = due; continue; }
                    current.Retries.Remove(attempt.TaskId);
                    OfferWindowOpportunity(current, attempt.Source); return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { Record("时段重试未确认", error.Message + " 不会自动发送本次动作。", ActivityLevel.Warning); }
        finally
        {
            lock (statusGate)
                if (current.Retries.TryGetValue(attempt.TaskId, out var registered) && ReferenceEquals(registered, cancellation)) current.Retries.Remove(attempt.TaskId);
            cancellation.Dispose();
        }
    }
    private async Task DrainWindowRetriesAsync(CancellationToken token)
    {
        Task[] workers;
        lock (statusGate) { EndWindowWatch(windowWatch); workers = [.. windowRetryWorkers]; }
        await Task.WhenAll(workers).WaitAsync(token);
    }
}
