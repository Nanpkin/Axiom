using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsProcessIdentity
{
    internal static string ReadImagePath(IntPtr handle)
    {
        var buffer = new StringBuilder(32768);
        uint length = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Path.GetFullPath(buffer.ToString());
    }

    internal static string ReadOwnerSid(IntPtr handle)
    {
        if (!OpenProcessToken(handle, 0x0008, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value ?? throw new UnauthorizedAccessException("Process has no owner SID.");
        }
        finally { CloseHandle(token); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
        StringBuilder path, ref uint length);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
