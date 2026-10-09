using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsForegroundWindowLayout
{
    public static Task DrainAsync() => WindowsForegroundWindowCommand.DrainAsync();
    public static Task<(bool Success, string Reason)> ApplyAsync(string layout, string sid, CancellationToken token)
    {
        if (!WindowLayoutGeometry.IsLayout(layout)) throw new ArgumentException("前台窗口布局无效。");
        return WindowsForegroundWindowCommand.RunAsync(sid,
            (window, cancellation) => ForegroundWindowLayoutOperation.ApplyAsync(window, layout, cancellation), token);
    }
}
