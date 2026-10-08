using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsUsageWindows
{
    public static async Task RunAsync(string[] excluded, Action<DateTimeOffset, string, string?> publish, CancellationToken token)
    {
        using var current = Process.GetCurrentProcess(); using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException(); var session = current.SessionId;
        using var source = new WindowsWindowEvents();
        var history = new UsageWindowHistory();
        Exception? failure = null;
        if (!EnumWindows((window, _) => { try { Read(window, true); return true; } catch (Exception error) { failure = error; return false; } }, 0))
            throw failure ?? new Win32Exception(Marshal.GetLastWin32Error());
        await foreach (var window in source.Reader.ReadAllAsync(token)) Read(window, false);
        throw new IOException("窗口观察通知中断。");

        void Read(nint window, bool baseline)
        {
            token.ThrowIfCancellationRequested();
            var at = DateTimeOffset.UtcNow;
            if (!IsWindow(window) || !IsWindowVisible(window) || GetAncestor(window, 2) != window || GetWindow(window, 4) != 0)
            {
                if (history.Hide(window) is { } hidden && !baseline) publish(at, hidden.Kind, hidden.Executable);
                return;
            }
            GetWindowThreadProcessId(window, out var pid);
            var state = IsIconic(window) ? "minimized" : IsZoomed(window) ? "maximized" : "normal";
            // Moving/resizing a known window does not require another process metadata read.
            if (!baseline && history.Unchanged(window, (int)pid, state)) return;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.SessionId != session) { history.Forget(window); return; }
                var path = WindowsForegroundIdentity.Resolve(new((int)pid, at.ToFileTime()), sid, session);
                if (path is null) { history.Forget(window); if (!baseline) publish(at, "gap", null); return; }
                var name = Path.GetFileName(path);
                if (excluded.Contains(name, StringComparer.OrdinalIgnoreCase) || new[] { "Axiom.exe", "Axiom.ServiceHost.exe", "Axiom.SessionAgent.exe", "Axiom.WinUI.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase))
                { history.Forget(window); return; }
                if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var latestPid) == 0 || latestPid != pid)
                { history.Forget(window); if (!baseline) publish(at, "gap", null); return; }
                if (history.Update(window, path, state, baseline, (int)pid) is { } change) publish(at, change.Kind, change.Executable);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            { history.Forget(window); if (!baseline) publish(at, "gap", null); }
        }
    }
    private delegate bool EnumCallback(nint window, nint state);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool EnumWindows(EnumCallback callback, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
}
