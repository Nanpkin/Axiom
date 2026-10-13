using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsSessionLock
{
    private static readonly BoundedNativeCommand Commands = new("锁屏", TimeSpan.FromSeconds(5));
    public static Task DrainAsync() => Commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    public static Task<(bool Success, string Reason)> LockAsync(CancellationToken token) =>
        Commands.RunAsync(cancellation => SessionLockOperation.Apply(new Api(cancellation), cancellation), token);

    public static bool? ReadLocked(CancellationToken token) => new Api(token).GetCurrentState() switch
    { SessionLockState.Locked => true, SessionLockState.Unlocked => false, _ => null };

    private sealed class Api(CancellationToken token) : ISessionLockApi
    {
        public SessionLockState GetCurrentState()
        {
            token.ThrowIfCancellationRequested();
            if (!Environment.UserInteractive) return SessionLockState.NonInteractive;
            using var current = Process.GetCurrentProcess(); var sessionId = current.SessionId;
            if (sessionId <= 0) return SessionLockState.NonInteractive;
            var accepted = WTSQuerySessionInformation(0, sessionId, 25, out var allocation, out var size); // WTSInfoEx
            try
            {
                token.ThrowIfCancellationRequested();
                if (!accepted || allocation == 0 || size < 20) return SessionLockState.Unknown;
                var header = new byte[20]; Marshal.Copy(allocation, header, 0, header.Length);
                return SessionLockStateReader.Parse(header, sessionId);
            }
            finally { if (allocation != 0) WTSFreeMemory(allocation); }
        }
        public (bool Accepted, int Error) RequestLock()
        {
            token.ThrowIfCancellationRequested();
            var accepted = LockWorkStation(); return (accepted, accepted ? 0 : Marshal.GetLastPInvokeError());
        }
    }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformation(nint server, int session, int information, out nint allocation, out int size);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint allocation);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LockWorkStation();
}
