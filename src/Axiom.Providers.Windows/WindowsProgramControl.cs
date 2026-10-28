using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

public sealed record ProgramControlResult(bool Success, string Reason)
{
    public bool Skipped { get; init; }
}

[SupportedOSPlatform("windows")]
public static class WindowsProgramControl
{
    public static ProgramControlResult Start(string path, string arguments, string workingDirectory, string window, bool skipRunning, CancellationToken token = default, int startupProtectionSeconds = 15)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return new(false, "目标程序不存在，请重新选择路径。");
        var directory = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(path)! : workingDirectory;
        if (!Directory.Exists(directory)) return new(false, "工作目录不存在。");
        var existing = Matching(path);
        try
        {
            token.ThrowIfCancellationRequested();
            if (skipRunning && existing.Count > 0) return new(true, "程序已运行，按配置跳过启动。") { Skipped = true };
            if (window == "startup-hidden") return WindowsStartupWindowGuard.Start(path, arguments, directory, startupProtectionSeconds, token);
            using var started = Process.Start(new ProcessStartInfo(path, arguments)
            {
                UseShellExecute = false, WorkingDirectory = directory,
                WindowStyle = window switch { "hidden" => ProcessWindowStyle.Hidden, "minimized" => ProcessWindowStyle.Minimized, _ => ProcessWindowStyle.Normal },
                CreateNoWindow = window == "hidden"
            });
            return started is null ? new(false, "程序启动未返回进程信息。") : new(true, "已发出启动请求：" + Path.GetFileName(path) + "（PID " + started.Id + "）。");
        }
        finally { foreach (var process in existing) process.Dispose(); }
    }

    public static async Task<ProgramControlResult> CloseAsync(string path, bool force, bool latest, TimeSpan timeout, CancellationToken token)
    {
        var matches = Matching(path);
        try
        {
            if (matches.Count == 0) return new(true, "程序未运行，无需关闭。");
            var selected = latest ? matches.OrderByDescending(p => p.StartTime).Take(1).ToArray() : matches.ToArray();
            foreach (var process in selected)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) continue;
                if (force) process.Kill();
                else if (!process.CloseMainWindow()) return new(false, "程序没有可正常关闭的主窗口；如需结束后台程序，请明确选择强制结束。");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
            try { await Task.WhenAll(selected.Select(p => p.WaitForExitAsync(deadline.Token))); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, "等待退出超时；程序可能正在询问是否保存，请查看目标窗口。"); }
            return new(true, $"已{(force ? "强制结束" : "关闭")} {selected.Length} 个程序实例：{Path.GetFileName(path)}。");
        }
        finally { foreach (var process in matches) process.Dispose(); }
    }

    internal static List<Process> Matching(string path)
    {
        var result = new List<Process>();
        var currentSession = Process.GetCurrentProcess().SessionId;
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException();
        try
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            {
                var retained = false;
                try
                {
                    if (process.SessionId != currentSession || process.HasExited) continue;
                    // Pin the handle before checking identity; later operations use this process instance.
                    var handle = process.SafeHandle;
                    var buffer = new System.Text.StringBuilder(32768); uint length = (uint)buffer.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!string.Equals(Path.GetFullPath(buffer.ToString()), path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!OpenProcessToken(handle, 8, out var ownerToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    using (ownerToken)
                    using (var owner = new WindowsIdentity(ownerToken.DangerousGetHandle()))
                        if (owner.User?.Value != sid) continue;
                    result.Add(process); retained = true;
                }
                catch (InvalidOperationException) { /* exited before inspection */ }
                finally { if (!retained) process.Dispose(); }
            }
            return result;
        }
        catch { foreach (var process in result) process.Dispose(); throw; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, System.Text.StringBuilder path, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
