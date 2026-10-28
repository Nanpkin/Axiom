namespace Axiom.Domain;

public enum ActivityLevel { Information, Success, Warning, Error }

public sealed record ActivityEvent(DateTimeOffset AtUtc, string Category, string Title,
    string Detail, ActivityLevel Level = ActivityLevel.Information, string? Subject = null);

public sealed record ActivityRecord(long Id, ActivityEvent Event,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? DetailFingerprint = null);
public sealed record ActivityPage(ActivityRecord[] Records, long? BeforeId, string? Error = null);
public sealed record ActivityCleanupResult(int DeletedCount, DateTimeOffset BeforeUtc);

public static class ActivityText
{
    public static string Summary(string detail, int maximumLength = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        var text = detail.AsSpan().Trim();
        var end = text.IndexOfAny('\r', '\n');
        if (end >= 0) text = text[..end];
        return text.Length <= maximumLength ? text.ToString() : text[..(maximumLength - 1)].ToString() + "…";
    }
}
