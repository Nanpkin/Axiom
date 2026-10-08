using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

// Desktop events shared by window-presence rules and pending window commands.
[SupportedOSPlatform("windows")]
public sealed class WindowsWindowEvents : IDisposable
{
    private static readonly object Gate = new();
    private static Hub? shared;
    internal readonly Channel<nint> Events = Channel.CreateBounded<nint>(256);
    public ChannelReader<nint> Reader => Events.Reader;
    private readonly bool lifecycleOnly;
    private bool disposed;
    public WindowsWindowEvents(bool lifecycleOnly = false)
    {
        this.lifecycleOnly = lifecycleOnly;
        lock (Gate)
        {
            shared ??= new Hub();
            try { shared.Ready.Task.GetAwaiter().GetResult(); }
            catch { shared = null; throw; }
            shared.Clients.Add(this);
        }
    }
    public void Dispose()
    {
        Hub? last = null;
        lock (Gate)
        {
            if (disposed) return; disposed = true;
            Events.Writer.TryComplete();
            shared!.Clients.Remove(this);
            if (shared.Clients.Count != 0) return;
            // Do not join under Gate: the callback may be waiting for it.
            last = shared; shared = null;
            PostThreadMessage(last.ThreadId, 0x0012, 0, 0);
        }
        last.Stopped.Task.Wait(TimeSpan.FromSeconds(5));
    }
    private sealed class Hub
    {
        internal readonly HashSet<WindowsWindowEvents> Clients = [];
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal uint ThreadId;
        private readonly Callback callback;
        internal Hub() { callback = OnEvent; new Thread(Run) { IsBackground = true, Name = "Axiom window commands" }.Start(); }
        private void Run()
        {
            ThreadId = GetCurrentThreadId(); PeekMessage(out _, 0, 0, 0, 0);
            var hook = SetWinEventHook(0x8000, 0x800B, 0, callback, 0, 0, 0);
            if (hook == 0) { Ready.TrySetException(new Win32Exception(Marshal.GetLastWin32Error())); Stopped.TrySetResult(); return; }
            Ready.TrySetResult();
            try { while (GetMessage(out _, 0, 0, 0) > 0) { } }
            finally { UnhookWinEvent(hook); lock (Gate) foreach (var client in Clients) client.Events.Writer.TryComplete(new IOException("窗口通知已停止。")); Stopped.TrySetResult(); }
        }
        private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
        {
            if (kind is not (0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x800A or 0x800B)) return;
            if (window == 0 || objectId != 0 || childId != 0 || kind is 0x8000 or 0x8002 or 0x800A or 0x800B && GetAncestor(window, 2) != window) return;
            lock (Gate)
                foreach (var client in Clients)
                    if ((!client.lifecycleOnly || kind is 0x8000 or 0x8001 or 0x8002 or 0x8003) && !client.Events.Writer.TryWrite(window)) client.Events.Writer.TryComplete(new IOException("窗口事件队列已满，无法确认窗口操作；不会自动重试。"));
        }
    }
    private delegate void Callback(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint WParam,LParam; public uint Time; public int X,Y; public uint Private; }
    [DllImport("user32.dll", SetLastError=true)] private static extern nint SetWinEventHook(uint min,uint max,nint module,Callback callback,uint pid,uint tid,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message,nint window,uint min,uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message,nint window,uint min,uint max,uint flags);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread,uint message,nint w,nint l);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window,uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
