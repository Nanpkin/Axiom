using System.ComponentModel;

namespace Axiom.Providers.Windows;

// Coordinates the existing launch and event-driven tray action under the program command's
// 40-second budget. Slow startup consumes tray wait time rather than extending the command.
public static class WindowsProgramStart
{
    public static async Task<ProgramControlResult> ExecuteAsync(bool hideTrayIcon,
        Func<CancellationToken, Task<ProgramControlResult>> start,
        Func<TimeSpan, CancellationToken, Task<ProgramControlResult>> hideTray,
        CancellationToken token, TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var began = time.GetTimestamp();
        token.ThrowIfCancellationRequested();
        var started = await start(token);
        token.ThrowIfCancellationRequested();
        if (!started.Success || !hideTrayIcon) return started;
        // ifRunning=skip only skips launch; an explicitly requested tray action still applies
        // to the matching running program, including when it started through Windows login.
        var remaining = TimeSpan.FromSeconds(35) - time.GetElapsedTime(began);
        if (remaining < TimeSpan.FromSeconds(1))
            return new(false, started.Reason + " 启动已占满本次等待预算，尚未隐藏托盘图标。不会关闭已运行的程序。");
        var wait = remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30);
        try
        {
            var hidden = await hideTray(wait, token);
            token.ThrowIfCancellationRequested();
            // HideAsync's no-icon outcome is a truthful no-op for a standalone hide action,
            // but does not confirm the explicitly requested startup-and-hide operation.
            return hidden.Success && !hidden.Skipped
                ? new(true, started.Reason + " 启动后托盘处理：" + hidden.Reason)
                : new(false, started.Reason + " 启动后托盘隐藏未确认：" + hidden.Reason + " 已运行的程序仍保留。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return new(false, started.Reason + " 启动后托盘隐藏失败：" + error.Message + " 已运行的程序仍保留。");
        }
    }
}
