using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

internal static class WindowLayoutGeometry
{
    internal static bool IsLayout(string? layout) => layout is "left" or "right" or "center" or "top-left" or "top-right" or "bottom-left" or "bottom-right";
    internal static string Classify(int x, int y, int width, int height, int left, int top, int workWidth, int workHeight, int tolerance)
    {
        if (width <= 0 || height <= 0 || tolerance < 0) throw new ArgumentException("窗口布局无效。");
        var margin = Math.Min(tolerance, Math.Min(workWidth, workHeight) / 20);
        foreach (var layout in new[] { "left", "right", "center" })
        {
            var target = Calculate(layout, left, top, workWidth, workHeight);
            if (Math.Abs((long)x - target.X) <= margin && Math.Abs((long)y - target.Y) <= margin &&
                Math.Abs((long)width - target.Width) <= margin && Math.Abs((long)height - target.Height) <= margin) return layout;
        }
        return "normal";
    }
    internal static (int X, int Y, int Width, int Height) Calculate(string layout, int left, int top, int width, int height)
    {
        if (width < 2 || height < 2) throw new ArgumentException("显示器工作区无效。");
        _ = checked(left + width); _ = checked(top + height);
        return layout switch
        {
            "left" => (left, top, width / 2, height),
            "right" => (checked(left + width / 2), top, width - width / 2, height),
            "top-left" => (left, top, width / 2, height / 2),
            "top-right" => (checked(left + width / 2), top, width - width / 2, height / 2),
            "bottom-left" => (left, checked(top + height / 2), width / 2, height - height / 2),
            "bottom-right" => (checked(left + width / 2), checked(top + height / 2), width - width / 2, height - height / 2),
            "center" => (checked(left + width / 10), checked(top + height / 10), width - 2 * (width / 10), height - 2 * (height / 10)),
            _ => throw new ArgumentException("窗口布局无效。")
        };
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsWindowLayout
{
    internal static string ReadLayout(nint window)
    {
        var previous = SetThreadDpiAwarenessContext(-4);
        if (previous == 0) throw new Win32Exception("无法确认窗口缩放，布局记录不完整。");
        try
        {
            var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor) || !GetWindowRect(window, out var rect))
                throw new Win32Exception("无法读取窗口工作区，布局记录不完整。");
            var dpi = GetDpiForWindow(window); if (dpi == 0) throw new Win32Exception("无法读取窗口缩放。");
            return WindowLayoutGeometry.Classify(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top, Math.Max(16, (int)dpi / 6));
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    internal static async Task<ProgramControlResult> ApplyAsync(nint window, int pid, string path, DateTime startedAtUtc,
        string layout, WindowsWindowEvents events, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var processes = WindowsProgramControl.Matching(path);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            // Keep this verified process handle alive across restoration and confirmation. Layout alone needs this extra fence.
            var process = processes.SingleOrDefault(p => p.Id == pid && WindowsWindowActions.TryReadCreation(p, out var creation) && creation == startedAtUtc);
            if (process is null || !OwnsWindow(window, process)) return new(false, "窗口所属进程已变化，未还原或调整布局。");
            if (!await WindowsWindowActions.RestoreNormalAsync(window, pid, events, deadline.Token)) return new(false, "无法还原窗口，未调整布局。");
            deadline.Token.ThrowIfCancellationRequested();
            if (!OwnsWindow(window, process)) return new(false, "窗口身份已变化，未调整布局。");
            (int X, int Y, int Width, int Height) target;
            DesktopMonitor monitor;
            var previous = SetThreadDpiAwarenessContext(-4); // physical pixels, restored before every await
            if (previous == 0) return new(false, "无法确认窗口的显示缩放，未调整布局。");
            try
            {
                monitor = ReadMonitor(window);
                target = WindowLayoutGeometry.Calculate(layout, monitor.Work.Left, monitor.Work.Top, monitor.Work.Width, monitor.Work.Height);
                if (!OwnsWindow(window, process) || ReadMonitor(window) != monitor)
                    return new(false, "窗口身份或当前工作区在操作期间变化；窗口可能已还原，未请求调整布局。");
                deadline.Token.ThrowIfCancellationRequested();
                if (!SetWindowPos(window, 0, target.X, target.Y, target.Width, target.Height, 0x0004 | 0x0010 | 0x4000))
                    return new(false, "系统未接受窗口布局请求；窗口可能已还原，布局未确认。");
            }
            finally { SetThreadDpiAwarenessContext(previous); }
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                previous = SetThreadDpiAwarenessContext(-4);
                if (previous == 0) return new(false, "无法确认窗口缩放，布局结果未确认。");
                try
                {
                    if (!OwnsWindow(window, process)) return new(false, "目标窗口已消失或身份变化，布局结果未确认。");
                    if (ReadMonitor(window) != monitor) return new(false, "当前显示器或工作区在请求后变化，布局结果未确认；不会恢复旧位置或自动重试。");
                    if (!IsIconic(window) && !IsZoomed(window) && GetWindowRect(window, out var rect) && rect.Left == target.X && rect.Top == target.Y &&
                        rect.Right - rect.Left == target.Width && rect.Bottom - rect.Top == target.Height)
                        return new(true, "已按当前显示器工作区整理窗口，位置及大小已读回确认；之后的手动调整不受干预。");
                }
                finally { SetThreadDpiAwarenessContext(previous); }
                await events.Events.Reader.ReadAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, "窗口布局未在期限内确认；窗口可能已还原或调整，程序可能限制大小或位置，不会自动重试。"); }
        catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException or OverflowException)
        { return new(false, "窗口布局未确认；窗口可能已还原或调整，不会自动重试。"); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static bool OwnsWindow(nint window, Process process) => !process.HasExited && IsWindowVisible(window) &&
        GetWindowThreadProcessId(window, out var owner) != 0 && owner == process.Id && GetAncestor(window, 2) == window && GetWindow(window, 4) == 0;
    private static DesktopMonitor ReadMonitor(nint window)
    {
        var handle = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (handle == 0 || !GetMonitorInfo(handle, ref info)) throw new Win32Exception("无法读取窗口当前显示器工作区。");
        return new(handle, new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
            new(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), (info.Flags & 1) != 0);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor,Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
}
