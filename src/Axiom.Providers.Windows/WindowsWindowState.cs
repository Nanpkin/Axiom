using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsWindowState : IWindowStateApi
{
    private readonly WindowsWindowEvents source;
    private readonly string sid;
    private readonly uint session;
    private readonly HashSet<string> names;
    public System.Threading.Channels.ChannelReader<nint> Events => source.Reader;

    public WindowsWindowState(string[] selectedPaths)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        sid = identity.User?.Value ?? throw new UnauthorizedAccessException("无法核验当前用户身份。");
        session = checked((uint)process.SessionId);
        names = selectedPaths.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        source = new WindowsWindowEvents(requireMinimizeEvents: true);
    }
    public void Dispose() => source.Dispose();

    public void Enumerate(Action<nint> visit)
    {
        Exception? failure = null;
        if (!EnumWindows((window, _) =>
            { try { visit(window); return true; } catch (Exception error) { failure = error; return false; } }, 0))
            throw failure ?? new Win32Exception(Marshal.GetLastWin32Error());
    }

    public WindowStateReading Read(nint window)
    {
        if (!Candidate(window)) return new(false, null, true, null);
        var state = ReadState(window);
        var thread = GetWindowThreadProcessId(window, out var pid);
        if (thread == 0 || pid == 0) return new(true, null, false, state);
        string? path = null;
        var at = DateTimeOffset.UtcNow.ToFileTime();
        try
        {
            // Only exclude definite unrelated names here; a matching name still requires full identity below.
            using (var owner = Process.GetProcessById(checked((int)pid)))
                if (owner.SessionId != session || !names.Contains(owner.ProcessName)) return new(false, null, true, null);
            // Pin the exact process while checking SID, image, lifetime and HWND ownership.
            using var handle = OpenProcess(0x00100000 | 0x1000, false, pid);
            if (handle.IsInvalid) return new(true, null, false, state);
            var native = handle.DangerousGetHandle();
            if (!ProcessIdToSessionId(pid, out var ownerSession)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (ownerSession != session || WindowsProcessIdentity.ReadOwnerSid(native) != sid)
                return new(false, null, true, null);
            if (!GetProcessTimes(handle, out var created, out _, out _, out _) || created > at)
                return new(true, null, false, state);
            path = WindowsProcessIdentity.ReadImagePath(native);
            if (!Candidate(window)) return new(false, null, true, null);
            state = ReadState(window);
            if (WaitForSingleObject(handle, 0) != 0x102 || GetWindowThreadProcessId(window, out var current) != thread || current != pid ||
                !ProcessIdToSessionId(pid, out ownerSession) || ownerSession != session)
                return new(true, null, false, state);
            return new(true, path, true, state);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            if (!Candidate(window)) return new(false, null, true, null);
            // A path read before a failed lifetime check must not identify a reused HWND/PID.
            return new(true, null, false, ReadState(window));
        }
    }

    private static bool Candidate(nint window) => IsWindow(window) && IsWindowVisible(window) && GetAncestor(window, 2) == window && GetWindow(window, 4) == 0;
    // A minimized maximized window keeps WS_MAXIMIZE; the visible state is minimized.
    private static WindowVisualState ReadState(nint window) => IsIconic(window) ? WindowVisualState.Minimized : IsZoomed(window) ? WindowVisualState.Maximized : WindowVisualState.Normal;
    private delegate bool EnumCallback(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint process, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
