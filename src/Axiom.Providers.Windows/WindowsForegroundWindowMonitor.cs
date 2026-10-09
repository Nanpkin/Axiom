using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsForegroundWindowMonitor
{
    public static Task DrainAsync() => WindowsForegroundWindowCommand.DrainAsync();
    public static Task<(bool Success, string Reason)> MoveAsync(string direction, string sid, CancellationToken token)
    {
        if (direction is not ("primary" or "previous" or "next")) throw new ArgumentException("前台窗口目标显示器无效。");
        return WindowsForegroundWindowCommand.RunAsync(sid,
            (window, cancellation) => ForegroundWindowMonitorOperation.ApplyAsync(window, direction, cancellation), token);
    }
}
