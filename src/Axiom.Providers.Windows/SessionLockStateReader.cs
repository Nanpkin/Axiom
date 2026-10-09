using System.Buffers.Binary;

namespace Axiom.Providers.Windows;

// WTSINFOEXW level 1, Win11 x64 SDK layout: Level at 0; 8-byte-aligned union starts at 8.
// Read only connection and lock metadata, not station/account/domain names or other session contents.
internal static class SessionLockStateReader
{
    internal static SessionLockState Parse(ReadOnlySpan<byte> header, int sessionId)
    {
        if (header.Length < 20 || sessionId <= 0 || BinaryPrimitives.ReadUInt32LittleEndian(header) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != sessionId) return SessionLockState.Unknown;
        if (BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != 0) return SessionLockState.NonInteractive; // WTSActive
        return BinaryPrimitives.ReadInt32LittleEndian(header[16..]) switch
        {
            0 => SessionLockState.Locked, 1 => SessionLockState.Unlocked, _ => SessionLockState.Unknown
        };
    }
}
