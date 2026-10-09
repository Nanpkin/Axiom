using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsAudioEvents : IDisposable
{
    private static readonly object Gate = new();
    private static Hub? shared;
    private readonly Channel<string> queue = Channel.CreateBounded<string>(64);
    private readonly Hub owner;
    private readonly bool includeEndpointState;
    private bool disposed;
    public ChannelReader<string> Events => queue.Reader;
    public static int ActiveConsumers { get { lock (Gate) return shared?.Clients.Count ?? 0; } }
    public WindowsAudioEvents(bool includeEndpointState = false)
    {
        this.includeEndpointState = includeEndpointState;
        lock (Gate)
        {
            // Keep a blocked native shutdown bounded to one hub, rather than creating more COM threads on retries.
            if (shared is { Closing: true }) throw new IOException("音频通知仍在结束，请稍后重新连接。");
            shared ??= new Hub(); owner = shared; owner.Clients.Add(this);
        }
        try
        {
            owner.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (owner.Stopped.Task.IsCompleted) throw new IOException("音频通知已停止。");
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        var last = false;
        lock (Gate)
        {
            if (disposed) return; disposed = true; queue.Writer.TryComplete(); owner.Clients.Remove(this);
            if (owner.Clients.Count == 0 && !owner.Stopped.Task.IsCompleted)
            { last = true; owner.Closing = true; owner.Stop.Set(); }
        }
        if (last) owner.Stopped.Task.Wait(TimeSpan.FromSeconds(5));
    }
    private sealed class Hub
    {
        internal readonly HashSet<WindowsAudioEvents> Clients = [];
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Stop = new();
        internal bool Closing;
        internal Hub()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "Axiom default audio events" };
            thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        }
        private void Publish(string kind)
        {
            lock (Gate) foreach (var client in Clients)
            {
                if (kind == "endpoints" && !client.includeEndpointState) continue;
                if (!client.queue.Writer.TryWrite(kind)) client.queue.Writer.TryComplete(new IOException("音频通知队列已满，状态未知。"));
            }
        }
        private void Run()
        {
            IAudioDeviceEnumerator? enumerator = null; var initialized = false; var registered = false;
            var sink = new NotificationSink(Publish, () => Publish("endpoints"));
            try
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0)); initialized = true;
                var cls = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"); var iid = typeof(IAudioDeviceEnumerator).GUID;
                Marshal.ThrowExceptionForHR(CoCreateInstance(ref cls, 0, 1, ref iid, out enumerator));
                Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(sink)); registered = true;
                Ready.TrySetResult(); Stop.Wait();
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                var failure = new IOException("默认音频设备通知不可用。", error); Ready.TrySetException(failure);
                lock (Gate) foreach (var client in Clients) client.queue.Writer.TryComplete(failure);
            }
            finally
            {
                if (registered) enumerator!.UnregisterEndpointNotificationCallback(sink);
                if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
                if (initialized) CoUninitialize();
                lock (Gate)
                {
                    foreach (var client in Clients) client.queue.Writer.TryComplete(new IOException("音频通知已停止。"));
                    if (ReferenceEquals(shared, this)) shared = null;
                    Stopped.TrySetResult(); Stop.Dispose();
                }
            }
        }
    }
    // Callbacks are notifications only. Device ID pointers and property payloads are never dereferenced.
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class NotificationSink(Action<string> publish, Action? endpointStateChanged = null) : IAudioNotificationClient
    {
        public int OnDeviceStateChanged(nint id, uint state) { endpointStateChanged?.Invoke(); return 0; }
        public int OnDeviceAdded(nint id) { endpointStateChanged?.Invoke(); return 0; }
        public int OnDeviceRemoved(nint id) { endpointStateChanged?.Invoke(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, nint id)
        {
            if (flow == 0 && role == 0) publish("output");
            else if (flow == 1 && role == 2) publish("input");
            return 0;
        }
        public int OnPropertyValueChanged(nint id, AudioPropertyKey property) => 0;
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out nint device);
        [PreserveSig] int GetDevice(nint id, out nint device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IAudioNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IAudioNotificationClient client);
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid cls, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAudioDeviceEnumerator instance);
}

[StructLayout(LayoutKind.Sequential)]
public struct AudioPropertyKey { public Guid Format; public uint Id; }

[ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged(nint id, uint state);
    [PreserveSig] int OnDeviceAdded(nint id);
    [PreserveSig] int OnDeviceRemoved(nint id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, nint id);
    [PreserveSig] int OnPropertyValueChanged(nint id, AudioPropertyKey property);
}
