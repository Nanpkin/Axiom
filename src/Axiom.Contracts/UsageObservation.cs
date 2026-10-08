namespace Axiom.Contracts;

// Only these metadata events cross the Agent boundary. Titles, arguments and content have no field.
public sealed record UsageSample(DateTimeOffset AtUtc, string Kind, string? Executable = null, string Origin = "unknown");
public sealed record UsageOptions(int Minutes, bool Foreground, bool ProcessStarts, string[] ExcludedApplications, string Purpose = "");
public sealed record UsageEvent(DateTimeOffset AtUtc, string Kind, string? ObjectId, string Origin, int Count = 1);
public sealed record UsageObject(string Id, string Executable);
public sealed record UsageSession(string Id, DateTimeOffset StartedUtc, DateTimeOffset EndsUtc, string State,
    UsageOptions Options, UsageObject[] Objects, UsageEvent[] Events, string[] Gaps);
public sealed record UsageSnapshot(UsageSession[] Sessions, string? ActiveId, DateTimeOffset ReadAtUtc, UsageSession? Selected = null, int TotalEvents = 0, UsageSuggestion[]? Suggestions = null);
public sealed record UsageRequest(string Operation, UsageOptions? Options = null, string? SessionId = null, string? Text = null, string? FeedbackId = null, int Offset = 0);
public sealed record UsageSuggestion(string Id, string Kind, string Title, string Evidence, string Limitation,
    string[] ObjectIds, int Occurrences, int Opportunities);

public static class UsageValidation
{
    public const int MaximumEvents = 4096;
    public const int MaximumObjects = 128;
    public const int MaximumSessions = 12;
    public const int MaximumExportBytes = 65536;
    public static void Options(UsageOptions options)
    {
        if (options.Minutes is < 1 or > 120 || !options.Foreground && !options.ProcessStarts ||
            options.ExcludedApplications is null || options.ExcludedApplications.Length > 128 || options.Purpose is null || options.Purpose.Length > 500 ||
            options.ExcludedApplications.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 132 || !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || n.IndexOfAny(['/', '\\', ':', '\r', '\n']) >= 0))
            throw new ArgumentException("请选择观察范围、1–120 分钟及有效的排除程序名；目的最多 500 字。");
    }
    public static void Request(UsageRequest request)
    {
        if (request.Operation is not ("start" or "read" or "pause" or "resume" or "stop" or "delete" or "clear" or "feedback" or "export" or "import" or "extension" or "draft") ||
            request.Operation == "start" != (request.Options is not null) ||
            request.SessionId is not null && !Guid.TryParseExact(request.SessionId, "D", out _) ||
            request.Text is not null && request.Text.Length > MaximumExportBytes || request.FeedbackId is not null && request.FeedbackId.Length > 200 || request.Offset is < 0 or > MaximumEvents)
            throw new ArgumentException("观察请求无效。");
        if (request.Options is not null) Options(request.Options);
        if (request.Operation is "delete" or "feedback" or "export" or "import" or "extension" or "draft" && request.SessionId is null) throw new ArgumentException("请选择观察记录。");
        if (request.Operation == "feedback" && (request.Text is not ("accepted" or "ignored" or "irrelevant") || string.IsNullOrWhiteSpace(request.FeedbackId)))
            throw new ArgumentException("建议反馈无效。");
    }
}
