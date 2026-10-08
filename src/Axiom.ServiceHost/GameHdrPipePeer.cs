using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Axiom.ServiceHost;

internal static class GameHdrPipePeer
{
    internal static void RequireSameSession(NamedPipeServerStream pipe, string? executableName = null)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
            throw new UnauthorizedAccessException("Client process identity unavailable.");
        using var process = Process.GetProcessById(checked((int)pid));
        if (process.SessionId != Process.GetCurrentProcess().SessionId ||
            executableName is not null && !string.Equals(Path.GetFileName(process.MainModule?.FileName), executableName,
                StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Client process or session rejected.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
