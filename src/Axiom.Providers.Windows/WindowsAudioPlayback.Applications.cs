using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsAudioPlayback
{
    public static Task<(bool Success, string Reason)> SetApplicationsAsync(IReadOnlyCollection<string> paths, string operation, int percent, CancellationToken token)
    {
        if (paths.Count is < 1 or > 32 || paths.Any(p => !Path.IsPathFullyQualified(p) || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ||
            operation is not ("volume" or "mute" or "unmute") || percent is < 0 or > 100) throw new ArgumentException("应用音量配置无效。");
        var targets = paths.ToArray();
        return Commands.RunAsync(cancellation => ApplyApplications(targets, operation, percent, cancellation), token);
    }

    private static (bool Success, string Reason) ApplyApplications(string[] paths, string operation, int percent, CancellationToken token)
    {
        IDeviceEnumerator? enumerator = null; IDevice? device = null; object? activated = null; IAudioSessionEnumerator? sessions = null;
        var initialized = false; var adapters = new List<Session>();
        try
        {
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0)); initialized = true;
            var cls = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"); var iid = typeof(IDeviceEnumerator).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref cls, 0, 1, ref iid, out enumerator));
            token.ThrowIfCancellationRequested();
            var defaultResult = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
            if (defaultResult < 0) return (false, defaultResult == unchecked((int)0x80070490)
                ? "当前没有默认播放设备，应用音量本次未执行；请检查Windows声音设置。"
                : "默认播放设备无法访问，应用音量本次未执行；请检查Windows音频服务与权限。");
            iid = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out activated));
            Marshal.ThrowExceptionForHR(((IAudioSessionManager2)activated).GetSessionEnumerator(out sessions));
            Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));
            if (count is < 0 or > 4096) return (false, "音频会话数量超出可安全处理范围，本次未修改。");
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
            using var current = Process.GetCurrentProcess(); var sessionId = current.SessionId;
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.ThrowExceptionForHR(sessions.GetSession(i, out var control));
                adapters.Add(new Session(control, sid, sessionId, token));
            }
            return ApplicationAudioOperation.Apply(adapters, paths, operation, percent, token);
        }
        finally
        {
            foreach (var adapter in adapters) adapter.Dispose();
            if (sessions is not null) Marshal.ReleaseComObject(sessions);
            if (activated is not null) Marshal.ReleaseComObject(activated);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            if (initialized) CoUninitialize();
        }
    }

    private sealed class Session(object control, string ownerSid, int ownerSession, CancellationToken token) : IApplicationAudioSession, IDisposable
    {
        private SafeProcessHandle? process;
        private uint pinnedPid;
        private long creationTime;
        private string? pinnedPath;
        private Guid context = new("0DE0208A-126F-4C92-9CA8-4C0AD69ADBFC");
        // The observer uses this same pinned identity and only borrows the handle until unregister completes.
        internal bool ProcessExited => process is not null && WaitForSingleObject(process, 0) == 0;
        internal WaitHandle? BorrowProcessWait() => process is null ? null : new BorrowedProcessWait(process);
        private sealed class BorrowedProcessWait : WaitHandle
        {
            internal BorrowedProcessWait(SafeProcessHandle handle) => SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), ownsHandle: false);
        }
        private IAudioSessionControl2 Control => (IAudioSessionControl2)control;
        private ISimpleAudioVolume Volume => (ISimpleAudioVolume)control;
        public bool IsActive
        {
            get { token.ThrowIfCancellationRequested(); Marshal.ThrowExceptionForHR(Control.GetState(out var state)); token.ThrowIfCancellationRequested(); return state == 1; }
        }
        public AudioSessionIdentity Identify(out string? path)
        {
            token.ThrowIfCancellationRequested();
            path = null;
            // System sounds and sessions spanning multiple processes are never treated as an executable target.
            var systemSounds = Control.IsSystemSoundsSession();
            if (systemSounds == 0) return AudioSessionIdentity.Other;
            if (systemSounds != 1) return AudioSessionIdentity.Unknown;
            if (Control.GetProcessId(out var pid) != 0 || pid == 0) return AudioSessionIdentity.Unknown;
            if (process is not null)
            {
                if (pid != pinnedPid || WaitForSingleObject(process, 0) != 258 || !GetProcessTimes(process, out var created, out _, out _, out _) || created != creationTime)
                    return AudioSessionIdentity.Unknown;
                path = pinnedPath; return AudioSessionIdentity.Owned;
            }
            var candidate = OpenProcess(0x00101000, false, pid); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
            if (candidate.IsInvalid) { candidate.Dispose(); return AudioSessionIdentity.Unknown; }
            try
            {
                if (WaitForSingleObject(candidate, 0) != 258 || !ProcessIdToSessionId(pid, out var actualSession)) return AudioSessionIdentity.Unknown;
                if (actualSession != ownerSession) return AudioSessionIdentity.Other;
                if (!OpenProcessToken(candidate, 8, out var accessToken)) return AudioSessionIdentity.Unknown;
                using (accessToken)
                using (var identity = new WindowsIdentity(accessToken.DangerousGetHandle()))
                    if (identity.User?.Value != ownerSid) return AudioSessionIdentity.Other;
                var buffer = new StringBuilder(32768); uint length = (uint)buffer.Capacity;
                if (!QueryFullProcessImageName(candidate, 0, buffer, ref length) || !GetProcessTimes(candidate, out var created, out _, out _, out _)) return AudioSessionIdentity.Unknown;
                if (Control.GetProcessId(out var recheckedPid) != 0 || pid != recheckedPid || WaitForSingleObject(candidate, 0) != 258) return AudioSessionIdentity.Unknown;
                pinnedPath = Path.GetFullPath(buffer.ToString()); creationTime = created; pinnedPid = pid;
                process = candidate; candidate = null!; path = pinnedPath;
                return AudioSessionIdentity.Owned;
            }
            finally { candidate?.Dispose(); }
        }
        private void VerifyBeforeWrite()
        {
            if (Identify(out _) != AudioSessionIdentity.Owned || !IsActive) throw new InvalidOperationException("音频会话身份或播放状态已变化。");
        }
        public void SetVolume(float value) { VerifyBeforeWrite(); token.ThrowIfCancellationRequested(); Marshal.ThrowExceptionForHR(Volume.SetMasterVolume(value, ref context)); }
        public float GetVolume() { Marshal.ThrowExceptionForHR(Volume.GetMasterVolume(out var value)); return value; }
        public void SetMuted(bool value) { VerifyBeforeWrite(); token.ThrowIfCancellationRequested(); Marshal.ThrowExceptionForHR(Volume.SetMute(value, ref context)); }
        public bool GetMuted() { Marshal.ThrowExceptionForHR(Volume.GetMute(out var value)); return value; }
        public void Dispose() { process?.Dispose(); Marshal.ReleaseComObject(control); }
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(nint notification);
        [PreserveSig] int UnregisterSessionNotification(nint notification);
        [PreserveSig] int RegisterDuckNotification(nint session, nint notification);
        [PreserveSig] int UnregisterDuckNotification(nint notification);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object control);
    }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName(nint name, ref Guid context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath(nint path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(nint notification);
        [PreserveSig] int UnregisterAudioSessionNotification(nint notification);
        [PreserveSig] int GetSessionIdentifier(out nint identifier);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint identifier);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float volume, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float volume);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint pid, out int session);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
