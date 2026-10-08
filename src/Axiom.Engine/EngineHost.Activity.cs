using Axiom.Domain;

namespace Axiom.Engine;

public interface IActivityHistory
{
    // Fast, bounded enqueue only; must not perform storage I/O on the caller.
    void Record(ActivityEvent entry);
    Task<ActivityPage> ReadAsync(long? beforeId, int pageSize, CancellationToken token);
    Task<ActivityCleanupResult> ClearBeforeAsync(DateTimeOffset beforeUtc, CancellationToken token);
}

public sealed partial class EngineHost
{
    private IActivityHistory? activityHistory;
    private string? activityPrincipal;

    public void ConfigureActivityHistory(IActivityHistory history, string principal)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        lock (gate) { RequireRunning(); activityHistory = history; activityPrincipal = principal; }
    }

    public void RecordActivity(ActivityEvent entry)
    {
        IActivityHistory? history;
        lock (gate) { if (!running || disposed) return; history = activityHistory; }
        history?.Record(entry);
    }

    public Task<ActivityPage> ReadActivityAsync(string principal, long? beforeId, int pageSize, CancellationToken token)
    {
        if (beforeId is <= 0) throw new ArgumentException("活动记录位置无效。");
        if (pageSize is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return AuthorizedHistory(principal).ReadAsync(beforeId, pageSize, token);
    }

    public Task<ActivityPage> ReadActivityAsync(string principal, long? beforeId, CancellationToken token) =>
        ReadActivityAsync(principal, beforeId, 30, token);

    public Task<ActivityCleanupResult> ClearOldActivityAsync(string principal, DateTimeOffset nowUtc, CancellationToken token) =>
        AuthorizedHistory(principal).ClearBeforeAsync(nowUtc.AddDays(-7), token);

    private IActivityHistory AuthorizedHistory(string principal)
    {
        lock (gate)
        {
            RequireRunning();
            if (principal != activityPrincipal) throw new UnauthorizedAccessException("访问被拒绝。");
            return activityHistory ?? throw new InvalidOperationException("活动记录尚未就绪。");
        }
    }
}
