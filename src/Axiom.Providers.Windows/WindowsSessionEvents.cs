using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

// One hidden top-level window per Agent, shared by rule and explicit observation leases.
[SupportedOSPlatform("windows")]
public sealed class WindowsSessionEvents : IDisposable
{
    private static readonly object Gate = new();
    private static Hub? shared;
    private readonly Channel<string> queue = Channel.CreateBounded<string>(64);
    private bool disposed;
    public static int ActiveConsumers { get { lock (Gate) return shared?.Clients.Count ?? 0; } }
    public ChannelReader<string> Events => queue.Reader;
    public WindowsSessionEvents()
    {
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
            if (disposed) return; disposed = true; queue.Writer.TryComplete();
            shared!.Clients.Remove(this);
            if (shared.Clients.Count != 0) return;
            last = shared; shared = null; PostMessage(last.Window, 0x10, 0, 0);
        }
        last.Stopped.Task.Wait(TimeSpan.FromSeconds(5));
    }
    private sealed class Hub
    {
        internal readonly HashSet<WindowsSessionEvents> Clients = [];
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal nint Window;
        private readonly Procedure procedure;
        private byte supply = 255;
        internal Hub() { procedure = Handle; new Thread(Run) { IsBackground = true, Name = "Axiom session and power events" }.Start(); }
        private void Run()
        {
            var name = "AxiomSystemEvents-" + Guid.NewGuid().ToString("N");
            var instance = GetModuleHandle(null);
            var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Name = name, Instance = instance };
            var registered = false; var notified = false;
            try
            {
                if (RegisterClassEx(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); registered = true;
                Window = CreateWindowEx(0, name, "", 0, 0, 0, 0, 0, 0, 0, instance, 0);
                if (Window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!WTSRegisterSessionNotification(Window, 0)) throw new Win32Exception(Marshal.GetLastWin32Error()); notified = true;
                if (GetSystemPowerStatus(out var initial)) supply = initial.AcLine;
                Ready.TrySetResult();
                int result;
                while ((result = GetMessage(out var message, 0, 0, 0)) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
                if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception error)
            {
                Ready.TrySetException(error);
                lock (Gate) foreach (var client in Clients) client.queue.Writer.TryComplete(new IOException("系统通知中断。", error));
            }
            finally
            {
                if (notified) WTSUnRegisterSessionNotification(Window);
                if (Window != 0) DestroyWindow(Window);
                if (registered) UnregisterClass(name, instance);
                lock (Gate) foreach (var client in Clients) client.queue.Writer.TryComplete(new IOException("系统通知已停止。"));
                Stopped.TrySetResult();
            }
        }
        private nint Handle(nint window, uint message, nint w, nint l)
        {
            if (message == 0x02B1)
            {
                if (w == 7) Publish("locked"); else if (w == 8) Publish("unlocked");
                return 0;
            }
            if (message == 0x0218)
            {
                if (w == 0x12) Publish("resumed"); // automatic resume; do not also emit the interactive-resume broadcast
                if (w == 0x0A && GetSystemPowerStatus(out var status))
                {
                    var before = supply; supply = status.AcLine;
                    if (before != 255 && supply != before && supply is 0 or 1) Publish(supply == 1 ? "ac" : "battery");
                }
                return 1;
            }
            if (message == 0x10) { PostQuitMessage(0); return 0; }
            return DefWindowProc(window, message, w, l);
        }
        private void Publish(string kind)
        {
            lock (Gate) foreach (var client in Clients)
                if (!client.queue.Writer.TryWrite(kind)) client.queue.Writer.TryComplete(new IOException("系统事件队列已满，检测状态未知。"));
        }
    }
    private delegate nint Procedure(nint window,uint message,nint w,nint l);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct WindowClass { public uint Size,Style; public Procedure Procedure; public int ClassExtra,WindowExtra; public nint Instance,Icon,Cursor,Background; public string? Menu,Name; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W,L; public uint Time; public int X,Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte AcLine,BatteryFlag,BatteryPercent,SystemStatus; public uint LifeTime,FullLifeTime; }
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool UnregisterClass(string name,nint instance);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern nint CreateWindowEx(uint extra,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint DefWindowProc(nint window,uint message,nint w,nint l);
    [DllImport("user32.dll",SetLastError=true)] private static extern int GetMessage(out Message message,nint window,uint min,uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window,uint message,nint w,nint l);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("wtsapi32.dll",SetLastError=true)] private static extern bool WTSRegisterSessionNotification(nint window,uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint window);
}
