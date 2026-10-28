using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Axiom.Contracts;

public sealed record RuleDraftDto(string Id, long Version, string Title, string ConditionText,
    string ActionText, DateTimeOffset UpdatedAtUtc, bool Executable = false);

public sealed record RuleDraftSummary(string Id, long Version, string Title, DateTimeOffset UpdatedAtUtc);

public sealed record RuleDraftMessage(int Major, int Minor, int Schema, string Type, string RequestId,
    string Scope, string? Id = null, long ExpectedVersion = 0, string? Title = null,
    string? ConditionText = null, string? ActionText = null, DateTimeOffset? DeadlineUtc = null,
    RuleDraftDto? Draft = null, RuleDraftSummary[]? Items = null, long? CurrentVersion = null,
    string? ErrorCode = null, string? Error = null);

public static class RuleDraftProtocol
{
    public const string Scope = "user";
    public const int MaximumFrame = 65_536;
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static string PipeName(string sid, int? sessionId = null) => SessionPipeName.Create("axiom-rule-drafts-v1-", sid, sessionId);

    public static void ValidateRequest(RuleDraftMessage message, DateTimeOffset now)
    {
        if (message.Major != 1 || message.Minor != 0 || message.Schema != 1)
            throw new InvalidDataException("ProtocolVersionRejected");
        if (message.Scope != Scope || message.RequestId is null || message.RequestId.Length is < 1 or > 64)
            throw new UnauthorizedAccessException("ScopeOrRequestRejected");
        if (message.DeadlineUtc is not { } deadline || deadline <= now || deadline > now.AddSeconds(30))
            throw new InvalidDataException("RequestDeadlineRejected");
        if (message.Draft is not null || message.Items is not null || message.CurrentVersion is not null ||
            message.ErrorCode is not null || message.Error is not null)
            throw new InvalidDataException("UnexpectedReplyFields");
        switch (message.Type)
        {
            case "list" when message.Id is null && message.ExpectedVersion == 0 &&
                message.Title is null && message.ConditionText is null && message.ActionText is null:
                return;
            case "get" when Guid.TryParseExact(message.Id, "D", out _) && message.ExpectedVersion == 0 &&
                message.Title is null && message.ConditionText is null && message.ActionText is null:
                return;
            case "save" when Guid.TryParseExact(message.Id, "D", out _) && message.ExpectedVersion >= 0 &&
                message.Title is not null && message.ConditionText is not null && message.ActionText is not null:
                return;
            default: throw new InvalidDataException("OperationOrFieldsRejected");
        }
    }

    public static async Task<RuleDraftMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        if (!await ReadExactOrEndAsync(stream, prefix, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumFrame) throw new InvalidDataException("FrameSizeRejected");
        var bytes = new byte[length];
        if (!await ReadExactOrEndAsync(stream, bytes, cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException("Truncated frame.");
        return JsonSerializer.Deserialize<RuleDraftMessage>(bytes, StrictJson) ?? throw new InvalidDataException("Null message.");
    }

    public static async Task WriteAsync(Stream stream, RuleDraftMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaximumFrame) throw new InvalidDataException("FrameSizeRejected");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactOrEndAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var received = 0;
        while (received < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[received..], token).ConfigureAwait(false);
            if (count == 0) { if (received == 0) return false; throw new EndOfStreamException("Truncated frame."); }
            received += count;
        }
        return true;
    }
}
