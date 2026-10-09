using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record ForegroundHint(int ProcessId, long AtFileTime);

[SupportedOSPlatform("windows")]
public sealed class WindowsForegroundEvents : IDisposable
{
    private static readonly object Gate = new();
    private static Hub? shared;
    private readonly Hub owner;
    private readonly Channel<ForegroundHint> queue = Channel.CreateBounded<ForegroundHint>(128);
    private long baseline = long.MaxValue;
    private bool disposed;
    private volatile bool failed;
    public bool Failed => failed;
    public ChannelReader<ForegroundHint> Events => queue.Reader;
    public static int ActiveConsumers { get { lock (Gate) return shared?.Clients.Count ?? 0; } }
    public static ForegroundHint ReadCurrent()
    {
        var at = DateTimeOffset.UtcNow.ToFileTime(); GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return new((int)pid, at);
    }
    public WindowsForegroundEvents()
    {
        lock (Gate) { shared ??= new Hub(); owner = shared; owner.Clients.Add(this); }
        try
        {
            owner.Ready.Task.GetAwaiter().GetResult();
            lock (Gate)
            {
                if (owner.Stopped.Task.IsCompleted) throw new IOException("前台通知源已停止。");
                baseline = DateTimeOffset.UtcNow.ToFileTime();
                GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
                queue.Writer.TryWrite(new((int)pid, baseline));
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        var last = false;
        lock (Gate)
        {
            if (disposed) return; disposed = true; queue.Writer.TryComplete(); owner.Clients.Remove(this);
            if (owner.Clients.Count == 0)
            {
                last = true; if (ReferenceEquals(shared, owner)) shared = null;
                if (!owner.Stopped.Task.IsCompleted) PostThreadMessage(owner.ThreadId, 0x12, 0, 0);
            }
        }
        if (last) owner.Stopped.Task.Wait(TimeSpan.FromSeconds(5));
    }
    private sealed class Hub
    {
        internal readonly HashSet<WindowsForegroundEvents> Clients = [];
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal uint ThreadId;
        private readonly Callback callback;
        internal Hub() { callback = OnEvent; new Thread(Run) { IsBackground = true, Name = "Axiom shared foreground events" }.Start(); }
        private void Run()
        {
            ThreadId = GetCurrentThreadId(); PeekMessage(out _, 0, 0, 0, 0);
            var hook = SetWinEventHook(3, 3, 0, callback, 0, 0, 0);
            if (hook == 0) { Ready.TrySetException(new Win32Exception(Marshal.GetLastWin32Error())); Stopped.TrySetResult(); return; }
            Ready.TrySetResult();
            try { while (GetMessage(out _, 0, 0, 0) > 0) { } }
            finally
            {
                UnhookWinEvent(hook);
                lock (Gate) foreach (var client in Clients) { client.failed = true; client.queue.Writer.TryComplete(new IOException("前台通知已停止。")); }
                Stopped.TrySetResult();
            }
        }
        private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
        {
            GetWindowThreadProcessId(window, out var pid);
            var elapsed = Math.Max(0, unchecked((int)((uint)Environment.TickCount64 - time)));
            var at = DateTimeOffset.UtcNow.AddMilliseconds(-elapsed).ToFileTime();
            lock (Gate) foreach (var client in Clients)
                if (at >= client.baseline && !client.queue.Writer.TryWrite(new((int)pid, at)))
                { client.failed = true; client.queue.Writer.TryComplete(new IOException("前台事件队列溢出，状态未知。")); }
        }
    }
    private delegate void Callback(nint hook,uint kind,nint window,int objectId,int childId,uint thread,uint time);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W,L; public uint Time; public int X,Y; public uint Private; }
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SetWinEventHook(uint min,uint max,nint module,Callback callback,uint pid,uint tid,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message,nint window,uint min,uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message,nint window,uint min,uint max,uint flags);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread,uint message,nint w,nint l);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
