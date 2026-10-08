namespace Axiom.Domain;

public enum ActivityLevel { Information, Success, Warning, Error }

public sealed record ActivityEvent(DateTimeOffset AtUtc, string Category, string Title,
    string Detail, ActivityLevel Level = ActivityLevel.Information, string? Subject = null);

public sealed record ActivityRecord(long Id, ActivityEvent Event);
public sealed record ActivityPage(ActivityRecord[] Records, long? BeforeId, string? Error = null);
public sealed record ActivityCleanupResult(int DeletedCount, DateTimeOffset BeforeUtc);
