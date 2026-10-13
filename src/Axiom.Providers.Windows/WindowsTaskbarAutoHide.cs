using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsTaskbarAutoHide
{
    private static readonly BoundedNativeCommand Commands = new("任务栏自动隐藏", TimeSpan.FromSeconds(5));
    public static Task DrainAsync() => Commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    public static Task<(bool Success, string Reason)> SetAsync(bool enabled, string sid, CancellationToken token) =>
        Commands.RunAsync(cancellation => { using var api = new ShellApi(sid, cancellation); return TaskbarAutoHideOperation.Apply(api, enabled, cancellation); }, token);

    private sealed class ShellApi : ITaskbarAutoHideApi, IDisposable
    {
        private readonly string sid;
        private readonly CancellationToken token;
        private readonly int session;
        private nint taskbar, desktop;
        private uint pid, thread;
        private long creation;
        private string? image;
        private SafeProcessHandle? process;
        internal ShellApi(string sid, CancellationToken token)
        {
            this.sid = sid; this.token = token;
            using var current = Process.GetCurrentProcess(); session = current.SessionId;
            try
            {
                if (!SessionCurrent()) return;
                desktop = GetShellWindow(); taskbar = FindWindow("Shell_TrayWnd", null);
                if (desktop == 0 || taskbar == 0) return;
                thread = GetWindowThreadProcessId(taskbar, out pid);
                if (thread == 0 || GetWindowThreadProcessId(desktop, out var shellPid) == 0 || pid != shellPid) return;
                process = OpenProcess(0x00101000, false, pid); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
                if (process.IsInvalid || !GetProcessTimes(process, out creation, out _, out _, out _)) return;
                image = WindowsProcessIdentity.ReadImagePath(process.DangerousGetHandle());
                // A similarly named arbitrary appbar or third-party shell is not a Windows taskbar target.
                if (!string.Equals(image, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase)) image = null;
            }
            catch { Dispose(); throw; }
        }
        public bool IsCurrent
        {
            get
            {
                token.ThrowIfCancellationRequested();
                return process is { IsInvalid: false } && image is not null && SessionCurrent() && WaitForSingleObject(process, 0) == 258 &&
                    GetProcessTimes(process, out var started, out _, out _, out _) && started == creation &&
                    ProcessIdToSessionId(pid, out var actualSession) && actualSession == session &&
                    WindowsProcessIdentity.ReadOwnerSid(process.DangerousGetHandle()) == sid &&
                    GetShellWindow() == desktop && FindWindow("Shell_TrayWnd", null) == taskbar &&
                    GetWindowThreadProcessId(taskbar, out var actualPid) == thread && actualPid == pid &&
                    GetWindowThreadProcessId(desktop, out var shellPid) != 0 && shellPid == pid;
            }
        }
        public uint? ReadState()
        {
            if (!IsCurrent || !Responsive()) return null;
            var data = Data(); var value = SHAppBarMessage(4, ref data); // ABM_GETSTATE; zero is a valid non-autohide state.
            token.ThrowIfCancellationRequested();
            // The documented state is ABS_AUTOHIDE | ABS_ALWAYSONTOP. Unknown bits must not be written back.
            return IsCurrent && (value & ~(nuint)3) == 0 ? (uint)value : null;
        }
        public bool SetState(uint state)
        {
            if ((state & ~3u) != 0 || !IsCurrent || !Responsive() || !IsCurrent) return false;
            token.ThrowIfCancellationRequested();
            var data = Data(); data.Parameter = (nint)state;
            SHAppBarMessage(10, ref data); // ABM_SETSTATE always returns TRUE; only readback can confirm the setting.
            token.ThrowIfCancellationRequested(); return IsCurrent;
        }
        private bool Responsive() => SendMessageTimeout(taskbar, 0, 0, 0, 0x0002 | 0x0020, 1000, out _) != 0; // WM_NULL to the verified shell, never broadcast.
        private TaskbarAppBarData Data() => new() { Size = (uint)Marshal.SizeOf<TaskbarAppBarData>(), Window = taskbar };
        private bool SessionCurrent()
        {
            token.ThrowIfCancellationRequested();
            using var identity = WindowsIdentity.GetCurrent();
            if (string.IsNullOrWhiteSpace(sid) || identity.User?.Value != sid || !Environment.UserInteractive || session <= 0) return false;
            var accepted = WTSQuerySessionInformation(0, session, 25, out var allocation, out var size);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!accepted || allocation == 0 || size < 20) return false;
                var header = new byte[20]; Marshal.Copy(allocation, header, 0, header.Length);
                return SessionLockStateReader.Parse(header, session) is SessionLockState.Locked or SessionLockState.Unlocked;
            }
            finally { if (allocation != 0) WTSFreeMemory(allocation); }
        }
        public void Dispose() { process?.Dispose(); process = null; }
    }
    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, ref TaskbarAppBarData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string cls, string? name);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nint w, nint l, uint flags, uint timeout, out nuint result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint processId, out int sessionId);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformation(nint server, int session, int information, out nint allocation, out int size);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint allocation);
}
