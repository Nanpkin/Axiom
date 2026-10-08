using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

internal static class WindowLayoutGeometry
{
    internal static (int X, int Y, int Width, int Height) Calculate(string layout, int left, int top, int width, int height)
    {
        if (width < 2 || height < 2) throw new ArgumentException("显示器工作区无效。");
        return layout switch
        {
            "left" => (left, top, width / 2, height),
            "right" => (checked(left + width / 2), top, width - width / 2, height),
            "center" => (checked(left + width / 10), checked(top + height / 10), width - 2 * (width / 10), height - 2 * (height / 10)),
            _ => throw new ArgumentException("窗口布局无效。")
        };
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsWindowLayout
{
    internal static async Task<ProgramControlResult> ApplyAsync(nint window, int pid, string path, string layout, WindowsWindowEvents events, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (IsIconic(window) || IsZoomed(window))
            {
                token.ThrowIfCancellationRequested();
                if (!ShowWindowAsync(window, 9)) return new(false, "无法还原窗口，未调整布局。");
                while (IsIconic(window) || IsZoomed(window)) await events.Events.Reader.ReadAsync(deadline.Token);
            }
            token.ThrowIfCancellationRequested();
            var processes = WindowsProgramControl.Matching(path);
            try
            {
                if (!processes.Any(p => p.Id == pid) || !IsWindowVisible(window) || GetWindowThreadProcessId(window, out var currentPid) == 0 || currentPid != pid)
                    return new(false, "窗口身份已变化，未调整布局。");
            }
            finally { foreach (var process in processes) process.Dispose(); }
            (int X, int Y, int Width, int Height) target;
            var previous = SetThreadDpiAwarenessContext(-4); // physical pixels, restored before every await
            if (previous == 0) return new(false, "无法确认窗口的显示缩放，未调整布局。");
            try
            {
                var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor)) return new(false, "无法读取当前显示器工作区。");
                target = WindowLayoutGeometry.Calculate(layout, monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
                if (!SetWindowPos(window, 0, target.X, target.Y, target.Width, target.Height, 0x0004 | 0x0010 | 0x4000)) return new(false, "系统未接受窗口布局请求。");
            }
            finally { SetThreadDpiAwarenessContext(previous); }
            bool Confirmed()
            {
                var prior = SetThreadDpiAwarenessContext(-4); if (prior == 0) return false;
                try
                {
                    return IsWindowVisible(window) && GetWindowThreadProcessId(window, out var owner) != 0 && owner == pid &&
                        GetWindowRect(window, out var rect) && rect.Left == target.X && rect.Top == target.Y && rect.Right - rect.Left == target.Width && rect.Bottom - rect.Top == target.Height;
                }
                finally { SetThreadDpiAwarenessContext(prior); }
            }
            while (!Confirmed()) await events.Events.Reader.ReadAsync(deadline.Token);
            return new(true, "已按当前显示器工作区整理窗口；不会持续固定布局或撤销之后的手动调整。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, "窗口未在期限内确认布局；程序可能限制大小或位置，不会自动重试。"); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor,Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window,int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
}
