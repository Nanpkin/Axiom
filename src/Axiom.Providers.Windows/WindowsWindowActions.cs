using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsWindowActions
{
    public static async Task<ProgramControlResult> CloseAsync(string path, string title, TimeSpan wait, CancellationToken token, string? state = null)
    {
        if (state is not (null or "minimize" or "maximize" or "restore" or "left" or "right" or "center" or "topmost" or "notopmost")) throw new ArgumentException("窗口状态无效。");
        if (wait < TimeSpan.FromSeconds(1) || wait > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(wait));
        if (!File.Exists(path)) return new(false, "目标程序不存在，请重新选择路径。");
        token.ThrowIfCancellationRequested();
        using var events = new WindowsWindowEvents(); // subscribe before the single initial enumeration
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(wait);
        nint target = 0; int pid = 0;
        try
        {
            var processes = WindowsProgramControl.Matching(path);
            try
            {
                var ids = processes.Select(p => p.Id).ToHashSet();
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var owner);
                    if (!ids.Contains((int)owner) || !Candidate(window, title)) return true;
                    target = window; pid = (int)owner; return false;
                }, 0);
            }
            finally { foreach (var process in processes) process.Dispose(); }
            while (target == 0)
            {
                var window = await events.Events.Reader.ReadAsync(deadline.Token);
                if (!Candidate(window, title)) continue;
                GetWindowThreadProcessId(window, out var owner); if (owner == 0) continue;
                // Ignore unrelated desktop windows before doing SID/path verification.
                try { using var process = Process.GetProcessById((int)owner); if (!process.ProcessName.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)) continue; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { continue; }
                var matches = WindowsProgramControl.Matching(path);
                try { if (!matches.Any(p => p.Id == owner)) continue; target = window; pid = (int)owner; }
                finally { foreach (var process in matches) process.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, $"等待窗口超时：{Path.GetFileName(path)} 没有匹配的可见主窗口；未结束程序。 "); }

        token.ThrowIfCancellationRequested();
        // Recheck identity immediately before sending, guarding exit, HWND and PID reuse.
        var current = WindowsProgramControl.Matching(path);
        try
        {
            if (!current.Any(p => p.Id == pid) || !Candidate(target, title) || GetWindowThreadProcessId(target, out var owner) == 0 || owner != pid)
                return new(false, "目标窗口已变化，未发出关闭请求。");
            if (state is "left" or "right" or "center") return await WindowsWindowLayout.ApplyAsync(target, pid, path, state, events, token);
            if (state is not null)
            {
                bool Confirmed() => Visible(target, pid) && (state == "minimize" ? IsIconic(target) : state == "maximize" ? IsZoomed(target) : state is "topmost" or "notopmost" ? Topmost(target) == (state == "topmost") : !IsIconic(target) && !IsZoomed(target));
                if (Confirmed()) return new(true, "窗口已处于所选状态；未重复操作。");
                using var stateDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); stateDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var accepted = state == "restore" ? await RestoreNormalAsync(target, pid, events, stateDeadline.Token)
                        : state is "topmost" or "notopmost" ? SetWindowPos(target, state == "topmost" ? -1 : -2, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x4000)
                        : ShowWindowAsync(target, state == "minimize" ? 6 : 3);
                    if (!accepted) return new(false, "无法更改窗口状态。");
                    while (!Confirmed()) await events.Events.Reader.ReadAsync(stateDeadline.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { if (!Confirmed()) return new(false, "已请求整理，但窗口状态未确认；不会自动重试。"); }
                return new(true, "窗口状态已确认；后续手动调整不受干预。");
            }
            if (!PostMessage(target, 0x0010, 0, 0)) return new(false, "无法请求关闭窗口：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            using var confirmation = CancellationTokenSource.CreateLinkedTokenSource(token); confirmation.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                while (Visible(target, pid)) await events.Events.Reader.ReadAsync(confirmation.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { if (Visible(target, pid)) return new(false, "已请求关闭，但窗口仍显示；程序可能需要确认。未强制结束，也不会自动重试。"); }
            var running = current.Any(p => { try { return !p.HasExited; } catch (InvalidOperationException) { return false; } });
            return new(true, $"窗口已关闭：{Path.GetFileName(path)}；" + (running ? "程序仍在运行。" : "目标程序处理关闭请求后已退出。"));
        }
        finally { foreach (var process in current) process.Dispose(); }
    }
    internal static async Task<bool> RestoreNormalAsync(nint window, int pid, WindowsWindowEvents events, CancellationToken token)
    {
        // A minimized maximized window first restores to maximized on Windows.
        // Normalize each state once, under the caller's single deadline.
        if (IsIconic(window))
        {
            token.ThrowIfCancellationRequested();
            if (!Visible(window, pid) || !ShowWindowAsync(window, 9)) return false;
            while (IsIconic(window)) await events.Events.Reader.ReadAsync(token);
        }
        if (IsZoomed(window))
        {
            token.ThrowIfCancellationRequested();
            if (!Visible(window, pid) || !ShowWindowAsync(window, 9)) return false;
            while (IsZoomed(window) || IsIconic(window)) await events.Events.Reader.ReadAsync(token);
        }
        return Visible(window, pid);
    }
    private static bool Topmost(nint window)
    {
        Marshal.SetLastPInvokeError(0);
        var style = GetWindowLongPtr(window, -20);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (style.ToInt64() & 0x0008) != 0;
    }
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW", SetLastError=true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    private static bool Candidate(nint window,string title)
    {
        if (!IsWindow(window) || !IsWindowVisible(window) || GetAncestor(window,2) != window || GetWindow(window,4) != 0) return false;
        if (title.Length == 0) return true;
        var text = new StringBuilder(1024); GetWindowText(window,text,text.Capacity);
        return text.ToString().Contains(title,StringComparison.OrdinalIgnoreCase);
    }
    private static bool Visible(nint window,int pid) => IsWindow(window) && IsWindowVisible(window) && GetWindowThreadProcessId(window,out var owner) != 0 && owner == pid;
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    private delegate bool EnumCallback(nint window,nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback,nint state);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window,uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window,uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(nint window,StringBuilder text,int count);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern bool PostMessage(nint window,uint message,nint w,nint l);
}
