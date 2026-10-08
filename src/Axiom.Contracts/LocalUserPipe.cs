using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Contracts;

/// <summary>Local IPC whose owner must be the actual user SID, including under UAC elevation.</summary>
[SupportedOSPlatform("windows")]
public static class LocalUserPipe
{
    public static NamedPipeClientStream Create(string name) => new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

    public static async Task ConnectForUserAsync(this NamedPipeClientStream pipe, int milliseconds, CancellationToken token)
    {
        await pipe.ConnectAsync(milliseconds, token).ConfigureAwait(false);
        try { RequireCurrentUser(pipe); }
        catch { pipe.Dispose(); throw; }
    }

    public static void RequireCurrentUser(NamedPipeClientStream pipe)
    {
        var result = GetSecurityInfo(pipe.SafePipeHandle, 6, 1, out var owner, out _, out _, out _, out var descriptor);
        if (result != 0) throw new UnauthorizedAccessException("无法核对管道所属用户，连接已拒绝。", new Win32Exception(checked((int)result)));
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (owner == 0 || identity.User is null || new SecurityIdentifier(owner) != identity.User)
                throw new UnauthorizedAccessException("管道不属于当前用户，连接已拒绝。");
        }
        finally { LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType, uint information,
        out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
