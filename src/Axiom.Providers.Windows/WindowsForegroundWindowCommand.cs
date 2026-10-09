using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsForegroundWindowCommand
{
    private static readonly BoundedNativeCommand Commands = new("前台窗口", TimeSpan.FromSeconds(5));
    internal sealed record Capture(nint Window, uint Pid, uint Thread, long AtFileTime);
    public static Task DrainAsync() => Commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    internal static Task<(bool Success, string Reason)> RunAsync(string sid, Func<Target, CancellationToken, Task<(bool Success, string Reason)>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Subscribe before the single capture: destruction in the capture/bind/admission gap invalidates it.
        var events = new WindowsWindowEvents(trackCaptureLifetime: true);
        try
        {
            token.ThrowIfCancellationRequested();
            var window = GetForegroundWindow();
            var thread = GetWindowThreadProcessId(window, out var pid);
            var capture = new Capture(window, pid, thread, DateTime.UtcNow.ToFileTimeUtc());
            if (window != 0) events.BindCapturedWindow(window);
            return RunCapturedAsync(capture, sid, events, action, token);
        }
        catch { events.Dispose(); throw; }
    }
    private static async Task<(bool Success, string Reason)> RunCapturedAsync(Capture capture, string sid,
        WindowsWindowEvents events, Func<Target, CancellationToken, Task<(bool Success, string Reason)>> action, CancellationToken token)
    {
        var ownership = 0;
        try
        {
            return await Commands.RunAsync(cancellation =>
            {
                if (Interlocked.CompareExchange(ref ownership, 1, 0) != 0)
                    return (false, "操作开始前已取消；未还原或移动。");
                try { return Apply(capture, sid, events, action, cancellation); }
                finally { events.Dispose(); }
            }, token);
        }
        finally
        {
            // Busy or not-yet-started cancellation releases immediately. An active worker keeps its lease to completion.
            if (Interlocked.CompareExchange(ref ownership, 2, 0) == 0) events.Dispose();
        }
    }
    private static (bool Success, string Reason) Apply(Capture capture, string sid, WindowsWindowEvents events, Func<Target, CancellationToken, Task<(bool Success, string Reason)>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (capture.Window == 0 || capture.Pid == 0 || capture.Thread == 0)
            return (false, "执行开始时没有可核验的前台窗口；未还原或移动。");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != sid) return (false, "当前用户身份与授权会话不一致；未还原或移动。");
        using var current = Process.GetCurrentProcess();
        using var process = OpenProcess(0x00101000, false, capture.Pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) return (false, "无法访问捕获窗口所属进程，可能已退出或权限不足；未还原或移动。");
        if (!GetProcessTimes(process, out var created, out _, out _, out _) || created > capture.AtFileTime)
            return (false, "窗口所属进程实例无法确认或已更换；未还原或移动。");
        var path = WindowsProcessIdentity.ReadImagePath(process.DangerousGetHandle());
        if (new[] { "Axiom.exe", "Axiom.ServiceHost.exe", "Axiom.SessionAgent.exe", "Axiom.WinUI.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            return (false, "不整理Axiom自身窗口；未还原或移动。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var target = new Target(capture, process, created, path, sid, current.SessionId, events);
        try { return action(target, deadline.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        { return (false, "前台窗口操作已取消或超过确认期限；可能已还原或移动，不重试、不恢复旧布局。"); }
    }

    internal sealed class Target(Capture capture, SafeProcessHandle process, long creation, string path, string sid, int session,
        WindowsWindowEvents events) : IForegroundLayoutWindow, IForegroundMonitorWindow
    {
        private DesktopMonitor? monitor;
        private (int X, int Y, int Width, int Height) target;
        public bool IsCurrent => !events.TrackedWindowDestroyed && GetForegroundWindow() == capture.Window &&
            IsWindow(capture.Window) && IsWindowVisible(capture.Window) && GetAncestor(capture.Window, 2) == capture.Window && GetWindow(capture.Window, 4) == 0 &&
            capture.Window != GetShellWindow() && capture.Window != GetDesktopWindow() && !IsTaskbar(capture.Window) &&
            GetWindowThreadProcessId(capture.Window, out var pid) == capture.Thread && pid == capture.Pid &&
            WaitForSingleObject(process, 0) == 258 && GetProcessTimes(process, out var created, out _, out _, out _) && created == creation &&
            ProcessIdToSessionId(capture.Pid, out var actualSession) && actualSession == session &&
            WindowsProcessIdentity.ReadOwnerSid(process.DangerousGetHandle()) == sid &&
            string.Equals(WindowsProcessIdentity.ReadImagePath(process.DangerousGetHandle()), path, StringComparison.OrdinalIgnoreCase);
        public Task<bool> RestoreAsync(CancellationToken token) => WindowsWindowActions.RestoreNormalAsync(capture.Window, (int)capture.Pid, events, token, () => IsCurrent);
        public bool Place(string layout, CancellationToken token)
        {
            var previous = BeginPixels();
            try
            {
                monitor = ReadMonitor(capture.Window);
                target = WindowLayoutGeometry.Calculate(layout, monitor.Work.Left, monitor.Work.Top, monitor.Work.Width, monitor.Work.Height);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent || ReadMonitor(capture.Window) != monitor) return false;
                return SetWindowPos(capture.Window, 0, target.X, target.Y, target.Width, target.Height, 0x0004 | 0x0010 | 0x4000);
            }
            finally { SetThreadDpiAwarenessContext(previous); }
        }
        public DesktopMonitor[] ReadMonitors() => WindowsWindowMonitor.ReadMonitors();
        public nint ReadCurrentMonitor()
        {
            var previous = BeginPixels();
            try { return ReadMonitor(capture.Window).Handle; }
            finally { SetThreadDpiAwarenessContext(previous); }
        }
        public Task<bool> RestoreAsync(DesktopMonitor[] before, CancellationToken token) =>
            WindowsWindowActions.RestoreNormalAsync(capture.Window, (int)capture.Pid, events, token,
                () => IsCurrent && WindowMonitorGeometry.SameTopology(before, WindowsWindowMonitor.ReadMonitors()));
        public bool Place(DesktopMonitor destination, DesktopMonitor[] before, CancellationToken token)
        {
            var previous = BeginPixels();
            try
            {
                monitor = destination; target = WindowMonitorGeometry.Center(destination);
                if (!IsCurrent || !WindowMonitorGeometry.SameTopology(before, WindowsWindowMonitor.ReadMonitors())) return false;
                token.ThrowIfCancellationRequested();
                return SetWindowPos(capture.Window, 0, target.X, target.Y, target.Width, target.Height, 0x0004 | 0x0010 | 0x4000);
            }
            finally { SetThreadDpiAwarenessContext(previous); }
        }
        public Task<bool> ConfirmAsync(CancellationToken token) => ConfirmCoreAsync(null, token);
        public Task<bool> ConfirmAsync(DesktopMonitor[] before, CancellationToken token) => ConfirmCoreAsync(before, token);
        private async Task<bool> ConfirmCoreAsync(DesktopMonitor[]? before, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var previous = BeginPixels();
                try
                {
                    if (before is not null && !WindowMonitorGeometry.SameTopology(before, WindowsWindowMonitor.ReadMonitors())) return false;
                    if (!IsCurrent || monitor is null || ReadMonitor(capture.Window) != monitor) return false;
                    if (!IsIconic(capture.Window) && !IsZoomed(capture.Window) && GetWindowRect(capture.Window, out var rect) &&
                        rect.Left == target.X && rect.Top == target.Y && rect.Right - rect.Left == target.Width && rect.Bottom - rect.Top == target.Height)
                        return true;
                }
                finally { SetThreadDpiAwarenessContext(previous); }
                await events.Events.Reader.ReadAsync(token); // bounded event wait, never a polling timer
            }
        }
    }
    private static bool IsTaskbar(nint window)
    {
        var name = new StringBuilder(256);
        if (GetClassName(window, name, name.Capacity) == 0) throw new Win32Exception("无法核验窗口类别。");
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }
    private static nint BeginPixels()
    {
        var previous = SetThreadDpiAwarenessContext(-4);
        return previous != 0 ? previous : throw new Win32Exception("无法确认窗口显示缩放。");
    }
    private static DesktopMonitor ReadMonitor(nint window)
    {
        var handle = MonitorFromWindow(window, 2); var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (handle == 0 || !GetMonitorInfo(handle, ref info)) throw new Win32Exception("无法读取窗口当前工作区。");
        return new(handle, new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom), new(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), (info.Flags & 1) != 0);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out int session);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
