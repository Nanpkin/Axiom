using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Axiom.Providers.Windows;

public static partial class WindowsWindowActions
{
    private static readonly BoundedNativeCommand RestoreCommands = new("可恢复窗口设置", TimeSpan.FromSeconds(5));
    public static async Task<IRestorableSettingSession> CreateRestorableSessionAsync(string path, CancellationToken token)
    {
        WindowSession? session = null;
        (bool Success, string Reason) result;
        try
        {
            result = await RestoreCommands.RunAsync(cancellation =>
            {
                var events = new WindowsWindowEvents(requireMinimizeEvents: true, trackCaptureLifetime: true, mergeHints: true);
                var processes = new List<Process>(); var transferred = false;
                try
                {
                    processes = WindowsProgramControl.Matching(path);
                    if (processes.Count > 128) throw new IOException("目标程序实例过多，未捕获窗口。");
                    Process? selected = null; nint window = 0; uint thread = 0; var enumerated = 0;
                    EnumWindows((candidate, _) =>
                    {
                        if (++enumerated > 4096) return false;
                        var candidateThread = GetWindowThreadProcessId(candidate, out var pid);
                        var process = processes.SingleOrDefault(p => p.Id == pid);
                        if (process is null || candidateThread == 0 || !Candidate(candidate, "")) return true;
                        selected = process; window = candidate; thread = candidateThread; return false;
                    }, 0);
                    if (selected is null || !TryReadCreation(selected, out var creation)) throw new IOException("没有可核验的当前主窗口；不等待或捕获后来的窗口。");
                    events.BindCapturedWindow(window);
                    cancellation.ThrowIfCancellationRequested();
                    session = new(window, thread, selected, creation, path, events);
                    processes.Remove(selected); transferred = true;
                    cancellation.ThrowIfCancellationRequested();
                    return (true, "已固定本轮主窗口并读取原WindowPlacement。");
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                    if (!transferred) events.Dispose();
                }
            }, token);
        }
        catch
        {
            await RestoreCommands.DrainAsync(); if (session is not null) await session.DisposeAsync(); throw;
        }
        if (result.Success && session is not null) return session;
        await RestoreCommands.DrainAsync(); if (session is not null) await session.DisposeAsync();
        throw new IOException(result.Reason);
    }
    private sealed class WindowSession : SerializedRestorableSettingSession
    {
        private readonly nint window;
        private readonly uint thread;
        private readonly Process process;
        private readonly DateTime creation;
        private readonly string path;
        private readonly string sid;
        private readonly int session;
        private readonly WindowsWindowEvents events;
        private readonly DesktopMonitor[] topology;
        private readonly Placement baseline;
        private Placement confirmed;
        private int relinquished;
        private bool released;
        public WindowSession(nint window, uint thread, Process process, DateTime creation, string path, WindowsWindowEvents events) : base(RestoreCommands)
        {
            this.window = window; this.thread = thread; this.process = process; this.creation = creation; this.path = path; this.events = events;
            sid = WindowsProcessIdentity.ReadOwnerSid(process.SafeHandle.DangerousGetHandle()); session = process.SessionId;
            topology = WindowsWindowMonitor.ReadMonitors();
            baseline = confirmed = Read();
        }
        private void Verify()
        {
            if (events.Failed || events.TrackedWindowDestroyed || !TryReadCreation(process, out var current) || current != creation ||
                GetWindowThreadProcessId(window, out var pid) != thread || pid != process.Id || !Candidate(window, "") ||
                !string.Equals(WindowsProcessIdentity.ReadImagePath(process.SafeHandle.DangerousGetHandle()), path, StringComparison.OrdinalIgnoreCase) ||
                WindowsProcessIdentity.ReadOwnerSid(process.SafeHandle.DangerousGetHandle()) != sid || process.SessionId != session ||
                !WindowMonitorGeometry.SameTopology(topology, WindowsWindowMonitor.ReadMonitors()))
                throw new IOException("本轮窗口身份、事件或显示拓扑已变化。");
        }
        private Placement Read()
        {
            Verify(); var previous = SetThreadDpiAwarenessContext(-4);
            if (previous == 0) throw new IOException("窗口缩放未知。");
            try
            {
                var placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
                if (!GetWindowPlacement(window, ref placement) || placement.Show is > 11 || (placement.Flags & ~7u) != 0 ||
                    placement.Normal.Right <= placement.Normal.Left || placement.Normal.Bottom <= placement.Normal.Top)
                    throw new IOException("WindowPlacement基线或读回未知。");
                return placement;
            }
            finally { SetThreadDpiAwarenessContext(previous); }
        }
        protected override RestorableSettingResult ApplyCore(IReadOnlyDictionary<string, string> parameters, CancellationToken token)
        {
            if (released || Volatile.Read(ref relinquished) != 0) return new(false, "本轮窗口控制已让出，不重新捕获或重试。", false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!Path.GetFullPath(parameters["executable"].Trim()).Equals(path, StringComparison.OrdinalIgnoreCase) || !Read().Equals(confirmed))
                { RelinquishCore(); return new(false, "本轮窗口目标或位置/状态已被其他来源改变，保留当前窗口，不证明是人为操作。", false); }
                var state = parameters["state"];
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                bool accepted;
                if (WindowLayoutGeometry.IsLayout(state))
                {
                    var result = WindowsWindowLayout.ApplyAsync(window, process.Id, path, creation, state, events, deadline.Token).GetAwaiter().GetResult();
                    accepted = result.Success;
                }
                else
                {
                    Verify(); deadline.Token.ThrowIfCancellationRequested();
                    accepted = state == "restore" ? RestoreNormalAsync(window, process.Id, events, deadline.Token, () => !events.TrackedWindowDestroyed).GetAwaiter().GetResult()
                        : state is "minimize" or "maximize" && ShowWindowAsync(window, state == "minimize" ? 6 : 3);
                    while (accepted && !(state == "minimize" ? IsIconic(window) : state == "maximize" ? IsZoomed(window) : !IsIconic(window) && !IsZoomed(window)))
                    { Verify(); events.Events.Reader.ReadAsync(deadline.Token).AsTask().GetAwaiter().GetResult(); }
                }
                token.ThrowIfCancellationRequested();
                if (!accepted || Volatile.Read(ref relinquished) != 0) throw new IOException("窗口设置未确认。");
                confirmed = Read();
                return new(true, "本轮固定主窗口状态/布局已读回确认；结束仅在完整placement仍相同时恢复。", true);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { RelinquishCore(); return new(false, "本轮窗口设置未确认或身份未知；可能已调整，不重试或恢复未确认写入。", false); }
        }
        protected override RestorableSettingResult ReleaseCore(CancellationToken token)
        {
            if (released) return new(true, "本轮窗口租约已释放。", false);
            released = true;
            if (Volatile.Read(ref relinquished) != 0) return new(true, "本轮窗口控制已让出；保留当前状态，未恢复。", false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!Read().Equals(confirmed)) return new(true, "窗口位置/状态已被其他来源改变，保留当前placement，未恢复。", false);
                if (confirmed.Equals(baseline)) return new(true, "本轮窗口仍为最初placement；无需恢复写入。", false);
                var previous = SetThreadDpiAwarenessContext(-4);
                if (previous == 0) throw new IOException("窗口缩放未知。");
                try
                {
                    Verify(); token.ThrowIfCancellationRequested(); var original = baseline;
                    if (!SetWindowPlacement(window, ref original)) throw new IOException("Windows未确认WindowPlacement恢复。");
                }
                finally { SetThreadDpiAwarenessContext(previous); }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                while (!Read().Equals(baseline)) events.Events.Reader.ReadAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
                return new(true, "本轮固定窗口原WindowPlacement已读回确认恢复；没有操作后来窗口。", false);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { return new(false, "原窗口/拓扑无法核验或恢复未确认；不操作新窗口、不重试。", false); }
        }
        protected override void RelinquishCore() => Interlocked.Exchange(ref relinquished, 1);
        protected override void DisposeCore() { events.Dispose(); process.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Placement { public uint Length, Flags, Show; public Point Min, Max; public PlacementRect Normal; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PlacementRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint window, ref Placement placement);
}
