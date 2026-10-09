using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsForegroundScreenCover : IForegroundScreenCoverSource
{
    private readonly Channel<ForegroundCoverHint> hints = Channel.CreateBounded<ForegroundCoverHint>(64);
    private readonly CancellationTokenSource stopping = new();
    private readonly WindowsForegroundEvents foreground;
    private readonly WindowsWindowEvents windows;
    private readonly WindowsSessionEvents displays;
    private readonly Task[] pumps;
    private readonly string sid;
    private readonly int session;
    private nint currentWindow;
    private long selectionEpoch;
    private int failed, disposed;
    public ChannelReader<ForegroundCoverHint> Events => hints.Reader;
    public bool Failed => Volatile.Read(ref failed) != 0 || foreground.Failed || windows.Failed || displays.Failed;
    public WindowsForegroundScreenCover()
    {
        using var identity = WindowsIdentity.GetCurrent(); using var process = Process.GetCurrentProcess();
        sid = identity.User?.Value ?? throw new UnauthorizedAccessException("无法核验当前用户身份。"); session = process.SessionId;
        // All sources exist before the first current-window read. No command admission or window changes.
        windows = new(requireMinimizeEvents: true, onDestroyed: Destroyed, requireCloakEvents: true);
        try { foreground = new(); }
        catch { windows.Dispose(); throw; }
        try { displays = new(displayOnly: true); }
        catch { foreground.Dispose(); windows.Dispose(); throw; }
        pumps = [PumpForegroundAsync(), PumpWindowsAsync(), PumpDisplaysAsync()];
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel(); hints.Writer.TryComplete();
        displays.Dispose(); foreground.Dispose(); windows.Dispose();
        // Cancellation closes reads; no native command worker or gate is held by this detector.
        Task.WhenAll(pumps).GetAwaiter().GetResult(); stopping.Dispose();
    }
    private void Destroyed(nint window)
    {
        if (window != Volatile.Read(ref currentWindow)) return;
        Interlocked.Increment(ref selectionEpoch); Hint(new(true));
    }
    private void Hint(ForegroundCoverHint hint)
    {
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref failed) != 0) return;
        if (!hints.Writer.TryWrite(hint)) Fail();
    }
    private void Fail()
    {
        Interlocked.Exchange(ref failed, 1);
        while (hints.Reader.TryRead(out _)) { }
        hints.Writer.TryComplete(new IOException("前台覆盖屏幕通知丢失，状态未知。"));
    }
    private async Task PumpForegroundAsync()
    {
        try
        {
            await foreach (var _ in foreground.Events.ReadAllAsync(stopping.Token))
            {
                if (foreground.Failed) { Fail(); return; }
                Interlocked.Increment(ref selectionEpoch); Hint(new());
            }
            if (!stopping.IsCancellationRequested) Fail();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { Fail(); }
    }
    private async Task PumpWindowsAsync()
    {
        try
        {
            await foreach (var window in windows.Reader.ReadAllAsync(stopping.Token))
            {
                if (windows.Failed) { Fail(); return; }
                if (window == Volatile.Read(ref currentWindow)) Hint(new());
            }
            if (!stopping.IsCancellationRequested) Fail();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { Fail(); }
    }
    private async Task PumpDisplaysAsync()
    {
        try
        {
            await foreach (var kind in displays.Events.ReadAllAsync(stopping.Token))
            {
                if (displays.Failed) { Fail(); return; }
                Hint(new(kind == "resumed"));
            }
            if (!stopping.IsCancellationRequested) Fail();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { Fail(); }
    }
    public ForegroundCoverReading Read()
    {
        // The short capture lease also rejects HWND destruction/reuse during this one geometry read.
        using var captureEvents = new WindowsWindowEvents(trackCaptureLifetime: true);
        var at = DateTime.UtcNow.ToFileTimeUtc(); var epoch = Interlocked.Read(ref selectionEpoch);
        var window = GetForegroundWindow(); Volatile.Write(ref currentWindow, window);
        if (window == 0) return new(null, false);
        captureEvents.BindCapturedWindow(window);
        var thread = GetWindowThreadProcessId(window, out var pid);
        if (thread == 0 || pid == 0 || !IsWindow(window)) return new(null, null);
        try
        {
            if (!Candidate(window)) return Stable(false, null);
            var className = new StringBuilder(256);
            if (GetClassName(window, className, className.Capacity) == 0) throw new Win32Exception("无法核验前台窗口类别。");
            if (window == GetShellWindow() || window == GetDesktopWindow() || className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return Stable(false, null);
            using var process = OpenProcess(0x00101000, false, pid);
            if (process.IsInvalid) return new(null, null);
            if (!GetProcessTimes(process, out var created, out _, out _, out _) || created > at ||
                !ProcessIdToSessionId(pid, out var actualSession)) return new(null, null);
            var owner = WindowsProcessIdentity.ReadOwnerSid(process.DangerousGetHandle());
            if (actualSession != session || owner != sid) return Stable(false, null);
            var path = WindowsProcessIdentity.ReadImagePath(process.DangerousGetHandle());
            var selection = new ForegroundCoverIdentity(window, pid, thread, created, epoch);
            if (new[] { "Axiom.exe", "Axiom.ServiceHost.exe", "Axiom.SessionAgent.exe", "Axiom.WinUI.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                return Stable(false, null);
            if (!ProcessCurrent()) return new(null, null);
            if (DwmGetWindowAttribute(window, 14, out uint cloaked, sizeof(uint)) < 0) return new(selection, null);
            if (cloaked != 0 || IsIconic(window)) return Stable(false, selection);
            var previousDpi = SetThreadDpiAwarenessContext(-4);
            if (previousDpi == 0) return new(selection, null);
            try
            {
                var before = WindowsWindowMonitor.ReadMonitors(); var monitor = MonitorFromWindow(window, 0);
                var selected = before.SingleOrDefault(m => m.Handle == monitor);
                if (selected is null || DwmGetWindowAttribute(window, 9, out Rect frame, Marshal.SizeOf<Rect>()) < 0) return new(selection, null);
                var covers = ForegroundScreenCoverGeometry.Covers(new(frame.Left, frame.Top, frame.Right, frame.Bottom), selected.Bounds);
                if (!WindowMonitorGeometry.SameTopology(before, WindowsWindowMonitor.ReadMonitors()) || MonitorFromWindow(window, 0) != monitor ||
                    DwmGetWindowAttribute(window, 9, out Rect confirmed, Marshal.SizeOf<Rect>()) < 0 || !frame.Equals(confirmed) ||
                    DwmGetWindowAttribute(window, 14, out cloaked, sizeof(uint)) < 0 || cloaked != 0 || !Candidate(window) || IsIconic(window) || !ProcessCurrent()) return new(selection, null);
                return Stable(covers, selection);
            }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
            bool ProcessCurrent() => WaitForSingleObject(process, 0) == 258 && GetProcessTimes(process, out var latest, out _, out _, out _) && latest == created &&
                ProcessIdToSessionId(pid, out var currentSession) && currentSession == session && WindowsProcessIdentity.ReadOwnerSid(process.DangerousGetHandle()) == sid &&
                string.Equals(WindowsProcessIdentity.ReadImagePath(process.DangerousGetHandle()), path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException or UnauthorizedAccessException or OverflowException)
        { return new(null, null); }
        ForegroundCoverReading Stable(bool value, ForegroundCoverIdentity? selection) =>
            Failed || captureEvents.Failed || captureEvents.TrackedWindowDestroyed || GetForegroundWindow() != window || !IsWindow(window) ||
            GetWindowThreadProcessId(window, out var current) != thread || current != pid || Interlocked.Read(ref selectionEpoch) != epoch ?
            new(selection, null) : new(selection, value);
    }
    private static bool Candidate(nint window) => IsWindowVisible(window) && GetAncestor(window, 2) == window && GetWindow(window, 4) == 0;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out int session);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
