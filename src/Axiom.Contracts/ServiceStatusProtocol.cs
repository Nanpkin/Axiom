using System.Buffers.Binary;
using System.Text.Json;

namespace Axiom.Contracts;

public sealed record ServiceMessage(int Major, int Minor, int Schema, string Type, string RequestId,
    string Scope, string Service, string? State = null, string? Error = null,
    DateTimeOffset? LastSuccessAt = null, long Revision = 0, bool Observing = false,
    DateTimeOffset? DeadlineUtc = null);

public static class ServiceStatusProtocol
{
    public const string ServiceName = "EventLog";
    public const string Scope = "machine";
    public const int MaximumFrame = 4096;
    public static string PipeName(string sid) => "axiom-status-v1-" + sid.Replace('-', '_');

    public static async Task<ServiceMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        if (!await ReadExactOrEndAsync(stream, prefix, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumFrame) throw new InvalidDataException("Frame size rejected.");
        var buffer = new byte[length];
        if (!await ReadExactOrEndAsync(stream, buffer, cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException("Truncated frame.");
        return JsonSerializer.Deserialize<ServiceMessage>(buffer) ?? throw new InvalidDataException("Null message.");
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

    public static async Task WriteAsync(Stream stream, ServiceMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaximumFrame) throw new InvalidDataException("Frame size rejected.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void ValidateRequest(ServiceMessage message, DateTimeOffset now)
    {
        if (message.Major != 1 || message.Minor != 0 || message.Schema != 1)
            throw new InvalidDataException("ProtocolVersionRejected");
        if (message.Scope != Scope || !string.Equals(message.Service, ServiceName, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("ScopeOrServiceRejected");
        if (message.RequestId is null || message.RequestId.Length is < 1 or > 64 ||
            message.Type is not ("observe" or "pause" or "retry"))
            throw new InvalidDataException("OperationRejected");
        if (message.DeadlineUtc is not { } deadline || deadline <= now || deadline > now.AddSeconds(30))
            throw new InvalidDataException("RequestDeadlineRejected");
        if (message.State is not null || message.Error is not null || message.LastSuccessAt is not null || message.Revision != 0 || message.Observing)
            throw new InvalidDataException("UnexpectedRequestFields");
    }
}
