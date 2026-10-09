using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

// Each lease owns an OS handle. Closing it releases only Axiom's requests, even if ClearRequest fails.
public sealed class WindowsPowerRequest : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly bool display;
    private bool systemSet, displaySet;
    private WindowsPowerRequest(string target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows power requests require Windows.");
        if (target is not ("system" or "display")) throw new ArgumentException("Invalid power request target.");
        display = target == "display";
        var reason = Marshal.StringToHGlobalUni("Axiom: active automation condition keeps " + (display ? "system and display" : "system") + " awake");
        try
        {
            var context = new ReasonContext { Version = 0, Flags = 1, SimpleReasonString = reason };
            handle = PowerCreateRequest(ref context);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "PowerCreateRequest failed."); }
        }
        finally { Marshal.FreeHGlobal(reason); }
        try
        {
            if (!PowerSetRequest(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "System power request rejected.");
            systemSet = true;
            if (display)
            {
                if (!PowerSetRequest(handle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Display power request rejected.");
                displaySet = true;
            }
        }
        catch { Dispose(); throw; }
    }
    public static IDisposable Acquire(string target) => new WindowsPowerRequest(target);
    public void Dispose()
    {
        if (handle.IsClosed) return;
        try
        {
            if (displaySet) { _ = PowerClearRequest(handle, 0); displaySet = false; }
            if (systemSet) { _ = PowerClearRequest(handle, 1); systemSet = false; }
        }
        finally { handle.Dispose(); }
    }
    // REASON_CONTEXT's union has a 24-byte detailed arm on x64, not merely the simple pointer.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct ReasonContext
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public nint SimpleReasonString;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerSetRequest(SafeFileHandle handle, int requestType);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerClearRequest(SafeFileHandle handle, int requestType);
}
