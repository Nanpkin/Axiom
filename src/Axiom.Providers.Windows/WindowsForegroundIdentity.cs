using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsForegroundIdentity
{
    public static string? Resolve(ForegroundHint hint, string ownerSid, int sessionId)
    {
        if (hint.ProcessId <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(hint.ProcessId);
            if (process.SessionId != sessionId) return null;
            var handle = OpenProcess(0x1000, false, hint.ProcessId);
            if (handle == 0) return null;
            try
            {
                if (WindowsProcessIdentity.ReadOwnerSid(handle) != ownerSid || !GetProcessTimes(handle, out var created, out _, out _, out _) || created > hint.AtFileTime) return null;
                return WindowsProcessIdentity.ReadImagePath(handle);
            }
            finally { CloseHandle(handle); }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint handle,out long created,out long exited,out long kernel,out long user);
}
