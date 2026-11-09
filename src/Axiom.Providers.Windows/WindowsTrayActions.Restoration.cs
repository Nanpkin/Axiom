using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Axiom.Providers.Windows;

public static partial class WindowsTrayActions
{
    private static readonly BoundedNativeCommand RestoreCommands = new("可恢复托盘图标", TimeSpan.FromSeconds(8));

    public static async Task<IRestorableSettingSession> CreateRestorableSessionAsync(string path, CancellationToken token)
    {
        TrayVisibilitySession? captured = null;
        (bool Success, string Reason) result;
        try
        {
            result = await RestoreCommands.RunAsync(cancellation =>
            {
                var matches = WindowsProgramControl.Matching(path); var transferred = false;
                try
                {
                    if (matches.Count != 1) throw new IOException("托盘恢复要求一个已运行的目标程序实例；不等待或控制后来的实例。");
                    captured = new(path, matches[0]);
                    transferred = true; cancellation.ThrowIfCancellationRequested();
                    return (true, "已固定本轮程序身份并读取最初托盘可见性，尚未修改。");
                }
                finally { if (!transferred) foreach (var process in matches) process.Dispose(); }
            }, token);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            await RestoreCommands.DrainAsync(); if (captured is not null) await captured.DisposeAsync();
            throw new RestorableSettingNotAcquiredException("托盘初始基线未捕获；本轮未写入，原生资源已清理。", error);
        }
        if (result.Success && captured is not null) return captured;
        await RestoreCommands.DrainAsync(); if (captured is not null) await captured.DisposeAsync();
        throw new RestorableSettingNotAcquiredException("托盘初始基线未捕获；本轮未写入，原生资源已清理。", new IOException(result.Reason));
    }

    private sealed class TrayVisibilitySession : SerializedRestorableSettingSession
    {
        private readonly string path;
        private readonly Process process;
        private readonly DateTime created;
        private readonly string sid;
        private readonly int session;
        private readonly Process shellProcess;
        private readonly DateTime shellCreated;
        private readonly nint shell;
        private readonly WindowsTrayEvents trayEvents;
        private readonly ConditionalSettingTransaction<bool> visibility;
        private CancellationToken currentToken;

        internal TrayVisibilitySession(string path, Process process) : base(RestoreCommands)
        {
            this.path = path; this.process = process;
            created = process.StartTime.ToUniversalTime(); session = process.SessionId;
            sid = WindowsProcessIdentity.ReadOwnerSid(process.SafeHandle.DangerousGetHandle());
            WindowsTrayEvents? tray = null; Process? explorer = null;
            try
            {
                trayEvents = tray = new WindowsTrayEvents();
                shell = FindWindowEx(0, 0, "Shell_TrayWnd", null);
                if (shell == 0 || GetWindowThreadProcessId(shell, out var shellPid) == 0) throw new IOException("系统托盘身份未知。");
                shellProcess = explorer = Process.GetProcessById((int)shellPid);
                _ = shellProcess.SafeHandle; shellCreated = shellProcess.StartTime.ToUniversalTime();
                var windows = FindOwnedWindows([process], CancellationToken.None).ToDictionary(w => w, w => GetWindowThreadProcessId(w, out _));
                if (windows.Count == 0 || windows.Values.Any(t => t == 0)) throw new IOException("目标程序没有可核验的当前托盘窗口。");
                visibility = new(Read(CancellationToken.None).Length > 0, () => Read(currentToken).Length > 0, Write, "本轮固定程序托盘可见性");
            }
            catch { tray?.Dispose(); explorer?.Dispose(); throw; }
        }

        private void Verify()
        {
            if (process.HasExited || process.StartTime.ToUniversalTime() != created || process.SessionId != session ||
                !WindowsProcessIdentity.ReadImagePath(process.SafeHandle.DangerousGetHandle()).Equals(path, StringComparison.OrdinalIgnoreCase) ||
                WindowsProcessIdentity.ReadOwnerSid(process.SafeHandle.DangerousGetHandle()) != sid || shellProcess.HasExited ||
                shellProcess.StartTime.ToUniversalTime() != shellCreated || FindWindowEx(0, 0, "Shell_TrayWnd", null) != shell ||
                GetWindowThreadProcessId(shell, out var shellPid) == 0 || shellPid != shellProcess.Id)
                throw new IOException("本轮程序或系统托盘身份变化，未操作替代目标。");
        }

        private (nint Window, uint Id, Guid Guid)[] Read(CancellationToken token)
        {
            return [.. ReadRegistered(token).Where(i => i.Visibility == IconVisibility.Visible).Select(i => i.Identity)];
        }

        private RegisteredIcon[] ReadRegistered(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Verify();
            var registered = FindRegistered(path, ReadRegistrations(), [process], token);
            Verify(); token.ThrowIfCancellationRequested(); return registered;
        }

        protected override RestorableSettingResult ApplyCore(IReadOnlyDictionary<string, string> parameters, CancellationToken token)
        {
            if (!Path.GetFullPath(parameters["executable"].Trim()).Equals(path, StringComparison.OrdinalIgnoreCase))
            { RelinquishCore(); return new(false, "托盘目标路径变化，未操作替代程序。", false); }
            currentToken = token;
            return visibility.Apply(true, token);
        }
        protected override RestorableSettingResult ReleaseCore(CancellationToken token)
        {
            currentToken = token;
            try
            {
                token.ThrowIfCancellationRequested();
                // The retained process handle identifies this lease's original instance. Its
                // confirmed exit is a completed empty release, never permission to bind a new PID.
                if (process.HasExited)
                {
                    visibility.Relinquish();
                    return new(true, "本轮原程序实例已退出，已无可恢复的原托盘目标，无需恢复；未操作同路径的新实例。", false);
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                visibility.Relinquish();
                return new(false, "无法确认本轮原程序实例是否已退出，未恢复托盘，也未操作同路径的新实例。", false);
            }
            return visibility.Release(token);
        }

        private void Write(bool visible)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(currentToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            if (visible)
            {
                var registered = ReadRegistered(deadline.Token);
                if (registered.Length > 0)
                {
                    var shown = SetVisibilityAsync(path, [.. registered.Select(i => i.Identity)], [process], true, trayEvents, deadline.Token, Verify).GetAwaiter().GetResult();
                    if (!shown.Success) throw new IOException(shown.Reason);
                    return;
                }
                var message = RegisterWindowMessage("TaskbarCreated");
                if (message == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Visibility belongs to the pinned process. DSX may replace its tray owner/UID
                // during connection; these are valid same-process changes, not replacement processes.
                foreach (var window in FindOwnedWindows([process], deadline.Token))
                {
                    Verify(); deadline.Token.ThrowIfCancellationRequested();
                    if (GetWindowThreadProcessId(window, out var pid) == 0 || pid != process.Id) throw new IOException("目标窗口身份变化。");
                    if (!PostMessage(window, message, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                while (true)
                {
                    registered = ReadRegistered(deadline.Token);
                    if (registered.Length > 0)
                    {
                        var recreated = SetVisibilityAsync(path, [.. registered.Select(i => i.Identity)], [process], true, trayEvents, deadline.Token, Verify).GetAwaiter().GetResult();
                        if (!recreated.Success) throw new IOException(recreated.Reason);
                        return;
                    }
                    trayEvents.Events.Reader.ReadAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
                }
            }
            // Hide the current visible set of this fixed process once. A replacement during
            // confirmation fails rather than deleting registration or chasing the new identity.
            var current = Read(deadline.Token);
            var hidden = SetVisibilityAsync(path, current, [process], false, trayEvents, deadline.Token, Verify).GetAwaiter().GetResult();
            if (!hidden.Success) throw new IOException(hidden.Reason);
            if (Read(deadline.Token).Length != 0) throw new IOException("恢复后又发现可见图标，未重复隐藏。");
        }
        protected override void RelinquishCore() => visibility.Relinquish();
        protected override void DisposeCore() { trayEvents.Dispose(); shellProcess.Dispose(); process.Dispose(); }
    }
}
