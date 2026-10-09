using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsWindowMonitor
{
    internal static DesktopMonitor[] ReadMonitors()
    {
        var previous = BeginPhysicalPixels();
        try { return ReadMonitorsCore(); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    private static DesktopMonitor[] ReadMonitorsCore()
    {
        var monitors = new List<DesktopMonitor>(); var unavailable = false;
        if (!EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info) || monitors.Count >= 64) { unavailable = true; return false; }
            monitors.Add(new(monitor, Rectangle(info.Monitor), Rectangle(info.Work), (info.Flags & 1) != 0)); return true;
        }, 0) || unavailable) throw new Win32Exception("无法读取完整显示器布局。");
        return WindowMonitorGeometry.Order(monitors);
    }
    internal static async Task<ProgramControlResult> ApplyAsync(nint window, int pid, string path, DateTime startedAtUtc,
        string direction, WindowsWindowEvents events, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var processes = WindowsProgramControl.Matching(path);
        try
        {
            // Matching pins the original process handle and verifies full path, SID and session.
            var process = processes.SingleOrDefault(p => p.Id == pid && !p.HasExited && p.StartTime.ToUniversalTime() == startedAtUtc);
            if (process is null || !OwnsWindow(window, process)) return new(false, "窗口所属进程已变化，未移动窗口。");
            DesktopMonitor[] before; DesktopMonitor destination; nint origin;
            var prior = BeginPhysicalPixels();
            try
            {
                before = ReadMonitorsCore(); origin = MonitorFromWindow(window, 2);
                destination = WindowMonitorGeometry.Select(before, origin, direction);
            }
            finally { SetThreadDpiAwarenessContext(prior); }
            if (before.Length == 1 && direction is "next" or "previous")
                return new(true, "只有一个显示器，已跳过移屏；窗口大小、位置和最小化/最大化状态均未改动。");
            if (!OwnsWindow(window, process)) return new(false, "窗口身份已变化，未还原或移动窗口。");
            if (!await WindowsWindowActions.RestoreNormalAsync(window, pid, events, deadline.Token)) return new(false, "无法还原窗口，未移动窗口。");
            deadline.Token.ThrowIfCancellationRequested();
            var target = WindowMonitorGeometry.Center(destination);
            prior = BeginPhysicalPixels();
            try
            {
                if (!WindowMonitorGeometry.SameTopology(before, ReadMonitorsCore()))
                    return new(false, "显示器布局在操作期间变化；窗口可能已还原，未请求移动，不会自动重试。");
                if (!OwnsWindow(window, process)) return new(false, "窗口身份已变化，未请求移动。");
                deadline.Token.ThrowIfCancellationRequested();
                if (!SetWindowPos(window, 0, target.X, target.Y, target.Width, target.Height, 0x0004 | 0x0010 | 0x4000))
                    return new(false, "系统未接受移屏请求；窗口可能已还原，移动结果未确认。");
            }
            finally { SetThreadDpiAwarenessContext(prior); }
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                prior = BeginPhysicalPixels();
                try
                {
                    if (!WindowMonitorGeometry.SameTopology(before, ReadMonitorsCore()))
                        return new(false, "显示器布局在请求后变化；移动结果未确认，不会自动重试或恢复旧位置。");
                    if (!OwnsWindow(window, process)) return new(false, "目标窗口已消失或身份变化，移动结果未确认。");
                    if (!IsIconic(window) && !IsZoomed(window) && MonitorFromWindow(window, 2) == destination.Handle &&
                        GetWindowRect(window, out var rect) && rect.Left == target.X && rect.Top == target.Y &&
                        rect.Right - rect.Left == target.Width && rect.Bottom - rect.Top == target.Height)
                        return new(true, "已将窗口移到" + (direction == "primary" ? "主显示器" : direction == "next" ? "下一个显示器" : "上一个显示器") +
                            "的工作区中央（80%），位置及大小已读回确认；后续手动调整不受干预。");
                }
                finally { SetThreadDpiAwarenessContext(prior); }
                await events.Events.Reader.ReadAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, "移屏结果未在期限内确认；窗口可能已还原或移动，程序可能限制位置/大小，不会自动重试。"); }
        catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException or OverflowException)
        { return new(false, "窗口移屏未确认：" + error.Message + " 窗口可能已还原或移动，不会自动重试。"); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static bool OwnsWindow(nint window, Process process) => !process.HasExited && IsWindow(window) && IsWindowVisible(window) &&
        GetWindowThreadProcessId(window, out var owner) != 0 && owner == process.Id && GetAncestor(window, 2) == window && GetWindow(window, 4) == 0;
    private static nint BeginPhysicalPixels()
    {
        var previous = SetThreadDpiAwarenessContext(-4);
        return previous != 0 ? previous : throw new Win32Exception("无法确认窗口显示缩放，未使用虚拟坐标移动窗口。");
    }
    private static MonitorRectangle Rectangle(Rect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint flags);
}
