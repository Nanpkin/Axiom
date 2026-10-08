using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Axiom.Contracts;

public sealed record GameHdrRuleDto(string Id, long Version, string Title, string ProcessName,
    string DisplayPath, bool HdrEnabled, bool Paused, string? ProcessNamesText = null);

public sealed record GameHdrDisplayDto(string Name, string Path, bool? Supported, bool? Enabled,
    bool? Active, string? Error);

public sealed record GameHdrStatusDto(string State, string Reason, DateTimeOffset AtUtc,
    bool AgentConnected, GameHdrDisplayDto[] Displays, GameHdrRuleDto[] Rules);

public sealed record GameHdrMessage(int Version, string Type, string RequestId,
    DateTimeOffset? DeadlineUtc = null, GameHdrRuleDto? Rule = null, GameHdrStatusDto? Status = null,
    string? Id = null, long? ExpectedVersion = null,
    GameHdrDisplayDto[]? Displays = null, string[]? ProcessNames = null,
    string? ProcessName = null, string? ProcessPath = null, int? ProcessId = null,
    DateTimeOffset? StartedAtUtc = null, bool? Running = null, string? DisplayPath = null,
    bool? Enabled = null, bool? ExpectedEnabled = null, string? Reason = null, string? Error = null, string? Generation = null,
    int Minor = 0, int Schema = 1, string Scope = "user-session");

public static class GameHdrProtocol
{
    public const int MaximumFrame = 65_536;
    public static string HostPipe(string sid) => "axiom-game-hdr-host-v1-" + sid.Replace('-', '_');
    public static string AgentPipe(string sid) => "axiom-game-hdr-agent-v1-" + sid.Replace('-', '_');
    private static readonly GameHdrJsonContext Strict = new(new JsonSerializerOptions
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });

    public static async Task<GameHdrMessage?> ReadAsync(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        if (!await ExactAsync(stream, prefix, token)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumFrame) throw new InvalidDataException("Invalid frame size.");
        var bytes = new byte[length];
        if (!await ExactAsync(stream, bytes, token)) throw new EndOfStreamException("Truncated frame.");
        return JsonSerializer.Deserialize(bytes, Strict.GameHdrMessage) ?? throw new InvalidDataException("Empty message.");
    }

    public static async Task WriteAsync(Stream stream, GameHdrMessage message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, GameHdrJsonContext.Default.GameHdrMessage);
        if (bytes.Length > MaximumFrame) throw new InvalidDataException("Frame too large.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    private static async Task<bool> ExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], token);
            if (count == 0) { if (offset == 0) return false; throw new EndOfStreamException("Truncated frame."); }
            offset += count;
        }
        return true;
    }
}
