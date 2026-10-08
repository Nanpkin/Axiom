using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Axiom.SessionAgent;

internal static class GameHdrPipePeer
{
    internal static void RequireHost(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
            throw new UnauthorizedAccessException("Host process identity unavailable.");
        using var process = Process.GetProcessById(checked((int)pid));
        if (process.SessionId != Process.GetCurrentProcess().SessionId ||
            !string.Equals(Path.GetFileName(process.MainModule?.FileName), "Axiom.ServiceHost.exe", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only the same-session Axiom host may control HDR.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
