using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsOpenTarget
{
    public static ProgramControlResult Open(string kind, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (kind == "folder")
        {
            if (!Path.IsPathFullyQualified(target) || target.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(target)) return new(false, "目录不存在或无法访问。");
        }
        else if (kind != "url" || !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            return new(false, "不支持此打开目标。");
        try
        {
            token.ThrowIfCancellationRequested();
            using var process = Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return new(true, "系统已接受打开请求；不保证页面加载完成，也不会自动关闭已打开的窗口。");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { return new(false, "无法打开目标：" + error.Message); }
    }
}
