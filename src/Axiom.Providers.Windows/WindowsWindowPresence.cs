using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsWindowPresence
{
    public static async Task ObserveAsync(string[] names, Action<WindowPresenceValue[]> publish, CancellationToken token)
    {
        using var process = Process.GetCurrentProcess(); using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException(); var session = process.SessionId;
        using var source = new WindowsWindowEvents(lifecycleOnly: true);
        var state = new WindowPresenceState(names);
        Exception? enumerationFailure = null;
        if (!EnumWindows((window, _) => { try { Read(window); return true; } catch (Exception error) { enumerationFailure = error; return false; } }, 0))
            throw enumerationFailure ?? new Win32Exception(Marshal.GetLastWin32Error());
        var previous = state.Samples(); publish(previous);
        await foreach (var window in source.Reader.ReadAllAsync(token))
        {
            Read(window); var samples = state.Samples();
            if (!samples.SequenceEqual(previous)) { publish(samples); previous = samples; }
        }
        throw new IOException("窗口通知已结束，检测状态未知。");

        void Read(nint window)
        {
            token.ThrowIfCancellationRequested();
            if (!IsWindow(window) || !IsWindowVisible(window) || GetAncestor(window, 2) != window || GetWindow(window, 4) != 0)
            { state.Set(window, false, null, true); return; }
            GetWindowThreadProcessId(window, out var pid);
            string? name = null;
            try
            {
                using var owner = Process.GetProcessById((int)pid);
                if (owner.SessionId != session) { state.Set(window, false, null, true); return; }
                name = owner.ProcessName + ".exe";
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) { state.Set(window, false, null, true); return; }
                var path = WindowsForegroundIdentity.Resolve(new((int)pid, DateTimeOffset.UtcNow.ToFileTime()), sid, session);
                // Resolve may race an exit/reuse. Verify the HWND still belongs to this PID before accepting it.
                var known = path is not null && IsWindowVisible(window) && GetWindowThreadProcessId(window, out var current) != 0 && current == pid;
                state.Set(window, true, name, known);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            { state.Set(window, true, name, false); }
        }
    }
    private delegate bool EnumCallback(nint window, nint state);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool EnumWindows(EnumCallback callback, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
}
