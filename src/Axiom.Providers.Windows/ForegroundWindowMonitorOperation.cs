namespace Axiom.Providers.Windows;

internal interface IForegroundMonitorWindow
{
    bool IsCurrent { get; }
    DesktopMonitor[] ReadMonitors();
    nint ReadCurrentMonitor();
    Task<bool> RestoreAsync(DesktopMonitor[] before, CancellationToken token);
    bool Place(DesktopMonitor destination, DesktopMonitor[] before, CancellationToken token);
    Task<bool> ConfirmAsync(DesktopMonitor[] before, CancellationToken token);
}

internal static class ForegroundWindowMonitorOperation
{
    internal static async Task<(bool Success, string Reason)> ApplyAsync(IForegroundMonitorWindow window, string direction, CancellationToken token)
    {
        if (direction is not ("primary" or "previous" or "next")) throw new ArgumentException("前台窗口目标显示器无效。");
        token.ThrowIfCancellationRequested();
        var attempted = false;
        try
        {
            if (!window.IsCurrent) return (false, "捕获的前台窗口已消失、切走或身份未知；未还原或移动。");
            var before = WindowMonitorGeometry.Order(window.ReadMonitors());
            var source = window.ReadCurrentMonitor();
            if (!before.Any(m => m.Handle == source)) return (false, "捕获窗口当前显示器无法核验；未还原或移动。");
            var destination = WindowMonitorGeometry.Select(before, source, direction);
            token.ThrowIfCancellationRequested();
            if (!window.IsCurrent || !WindowMonitorGeometry.SameTopology(before, window.ReadMonitors()))
                return (false, "前台窗口或显示器布局已变化；未还原或移动。");
            if (before.Length == 1 && direction is "previous" or "next")
                return (true, "只有一个显示器，已跳过前台窗口移屏；位置、大小和最小化/最大化状态均未改动。");
            attempted = true;
            if (!await window.RestoreAsync(before, token))
                return (false, "前台窗口身份、显示器布局或还原状态无法确认；可能已还原，未请求移屏。");
            token.ThrowIfCancellationRequested();
            if (!window.IsCurrent || !WindowMonitorGeometry.SameTopology(before, window.ReadMonitors()))
                return (false, "前台窗口或显示器布局已变化；可能已还原，未请求移屏，不会更换目标窗口或显示器。");
            if (!window.Place(destination, before, token))
                return (false, "系统拒绝移屏或窗口/显示器布局变化；可能已还原或移动，结果未确认。");
            token.ThrowIfCancellationRequested();
            if (!window.IsCurrent || !await window.ConfirmAsync(before, token))
                return (false, "捕获窗口的移屏位置及大小未读回确认；可能已还原或移动，不重试、不恢复。");
            return (true, "已将捕获的前台窗口放到" + (direction == "primary" ? "主显示器（已在主屏也会整理）" : direction == "previous" ? "上一个显示器" : "下一个显示器") +
                "工作区中央80%，位置及大小已读回确认；之后手动调整不受干预。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { return (false, attempted ? "前台窗口移屏未确认；可能已还原或移动，拓扑、身份、通知或权限可能失效，不重试、不恢复。" :
            "前台窗口身份或显示器布局无法核验，可能屏幕失联或权限不足；未还原或移动。"); }
    }
}
