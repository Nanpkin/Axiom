using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Win32;

namespace Axiom.Providers.Windows;

// Only pending commands subscribe. Notifications invalidate a snapshot; they never identify an icon.
[SupportedOSPlatform("windows")]
internal sealed class WindowsTrayEvents : IDisposable
{
    private static readonly object Gate = new();
    private static Hub? shared;
    internal readonly Channel<bool> Events = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private bool disposed;
    internal WindowsTrayEvents()
    {
        lock (Gate)
        {
            shared ??= new Hub();
            try { shared.Ready.Task.GetAwaiter().GetResult(); }
            catch { var failed = shared; shared = null; if (failed.Stopped.Task.Wait(TimeSpan.FromSeconds(5))) failed.Stop.Dispose(); throw; }
            shared.Clients.Add(this);
        }
    }
    public void Dispose()
    {
        Hub? last;
        lock (Gate)
        {
            if (disposed) return; disposed = true; Events.Writer.TryComplete();
            shared!.Clients.Remove(this); if (shared.Clients.Count != 0) return;
            last = shared; shared = null; last.Stop.Set();
        }
        if (last.Stopped.Task.Wait(TimeSpan.FromSeconds(5))) last.Stop.Dispose();
    }
    private sealed class Hub
    {
        internal readonly HashSet<WindowsTrayEvents> Clients = [];
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEvent Stop = new(false);
        private readonly Callback callback;
        internal Hub() { callback = OnEvent; new Thread(Run) { IsBackground = true, Name = "Axiom tray commands" }.Start(); }
        private void Publish(Exception? error = null)
        {
            lock (Gate) foreach (var client in Clients)
                if (error is null) client.Events.Writer.TryWrite(true); else client.Events.Writer.TryComplete(error);
        }
        private void Run()
        {
            nint hook = 0;
            try
            {
                using var registry = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings") ?? Registry.CurrentUser.OpenSubKey(@"Control Panel")
                    ?? throw new IOException("系统托盘记录不可用，无法等待图标。");
                using var changed = new AutoResetEvent(false);
                void Arm()
                {
                    // This dedicated thread stays alive for the entire registration.
                    var error = RegNotifyChangeKeyValue(registry.Handle.DangerousGetHandle(), true, 5, changed.SafeWaitHandle.DangerousGetHandle(), true);
                    if (error != 0) throw new Win32Exception(error);
                }
                Arm();
                PeekMessage(out _, 0, 0, 0, 0);
                using var current = Process.GetCurrentProcess();
                var explorers = Process.GetProcessesByName("explorer");
                try
                {
                    var explorer = explorers.FirstOrDefault(p => p.SessionId == current.SessionId);
                    if (explorer is not null)
                    {
                        hook = SetWinEventHook(0x8000, 0x800c, 0, callback, (uint)explorer.Id, 0, 0);
                        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
                finally { foreach (var process in explorers) process.Dispose(); }
                var handles = new[] { Stop.SafeWaitHandle.DangerousGetHandle(), changed.SafeWaitHandle.DangerousGetHandle() };
                Ready.TrySetResult();
                while (true)
                {
                    var signaled = MsgWaitForMultipleObjectsEx(2, handles, uint.MaxValue, 0x04ff, 4);
                    if (signaled == 0) break;
                    if (signaled == 1) { Arm(); Publish(); }
                    else if (signaled == 2) while (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
                    else throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch (Exception error) { if (!Ready.TrySetException(error)) Publish(error); }
            finally { if (hook != 0) UnhookWinEvent(hook); Stopped.TrySetResult(); }
        }
        private void OnEvent(nint hook,uint kind,nint window,int objectId,int childId,uint thread,uint time)
        { if (window != 0 && kind is 0x8000 or 0x8002 or 0x8004 or 0x800c) Publish(); }
    }
    private delegate void Callback(nint hook,uint kind,nint window,int objectId,int childId,uint thread,uint time);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint WParam,LParam; public uint Time; public int X,Y; public uint Private; }
    [DllImport("advapi32.dll")] private static extern int RegNotifyChangeKeyValue(nint key,[MarshalAs(UnmanagedType.Bool)]bool subtree,uint filter,nint signal,[MarshalAs(UnmanagedType.Bool)]bool async);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SetWinEventHook(uint min,uint max,nint module,Callback callback,uint pid,uint tid,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message,nint window,uint min,uint max,uint flags);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint MsgWaitForMultipleObjectsEx(uint count,nint[] handles,uint timeout,uint wakeMask,uint flags);
}
