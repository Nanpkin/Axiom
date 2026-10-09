using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsInternetConnectivityEvents : IInternetConnectivitySource
{
    private static readonly object Gate = new();
    private static Hub? shared;
    private readonly Hub owner;
    private readonly Channel<InternetConnectivitySample> queue = Channel.CreateBounded<InternetConnectivitySample>(64);
    private volatile bool failed;
    private bool disposed;
    public ChannelReader<InternetConnectivitySample> Events => queue.Reader;
    public bool Failed => failed;
    public static int ActiveConsumers { get { lock (Gate) return shared?.Clients.Count ?? 0; } }
    public WindowsInternetConnectivityEvents()
    {
        lock (Gate)
        {
            if (shared is { Closing: true }) throw new IOException("Internet通知仍在结束，请稍后重新启用规则。");
            shared ??= new Hub(); owner = shared; owner.Clients.Add(this);
            queue.Writer.TryWrite(owner.Latest with { Baseline = true });
        }
        // Construction never waits for COM activation. The shared worker subscribes before its initial query.
    }
    public void Dispose()
    {
        var last = false;
        lock (Gate)
        {
            if (disposed) return; disposed = true; queue.Writer.TryComplete(); owner.Clients.Remove(this);
            if (owner.Clients.Count == 0 && !owner.Stopped.Task.IsCompleted) { last = true; owner.Closing = true; }
        }
        if (last) { owner.Stop.Cancel(); owner.Hints.Complete(); owner.Stopped.Task.Wait(TimeSpan.FromSeconds(5)); }
    }
    private sealed class Hub
    {
        internal readonly HashSet<WindowsInternetConnectivityEvents> Clients = [];
        internal readonly InternetConnectivityNotifications Hints;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InternetConnectivitySample Latest = new(null, true, "正在连接Windows Internet状态通知。");
        internal bool Closing;
        internal Hub()
        {
            Hints = new(CompleteLostNotifications);
            var thread = new Thread(Run) { IsBackground = true, Name = "Axiom Windows Internet state" };
            thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        }
        private void CompleteLostNotifications()
        {
            lock (Gate)
            {
                Closing = true;
                foreach (var client in Clients)
                { client.failed = true; client.queue.Writer.TryComplete(new IOException("Internet状态通知丢失。")); }
            }
        }
        private void Publish(InternetConnectivitySample sample)
        {
            lock (Gate)
            {
                Latest = sample;
                foreach (var client in Clients)
                    if (!client.queue.Writer.TryWrite(sample))
                    { client.failed = true; client.queue.Writer.TryComplete(new IOException("Internet状态样本积压，检测状态未知。")); }
            }
        }
        private void Run()
        {
            var initialized = false; NativeApi? native = null;
            try
            {
                Stop.Token.ThrowIfCancellationRequested();
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0)); initialized = true;
                Stop.Token.ThrowIfCancellationRequested();
                native = new NativeApi();
                Stop.Token.ThrowIfCancellationRequested();
                native.Subscribe(new InternetConnectivityCallbackSink(Hints.Hint));
                InternetConnectivityObservation.Run(Hints.Events, native.Read, Publish, Stop.Token, () => Hints.Failed);
            }
            catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                lock (Gate) { Closing = true; foreach (var client in Clients) client.failed = true; }
                Publish(new(null, false, "Windows Internet状态服务、查询或通知不可用；请检查服务后暂停并重新启用规则。"));
                lock (Gate) foreach (var client in Clients) client.queue.Writer.TryComplete(new IOException("Windows Internet状态通知中断。"));
            }
            finally
            {
                try { native?.Dispose(); }
                finally
                {
                    if (initialized) CoUninitialize();
                    lock (Gate)
                    {
                        Closing = true;
                        foreach (var client in Clients) client.queue.Writer.TryComplete();
                        if (ReferenceEquals(shared, this)) shared = null;
                        Stopped.TrySetResult();
                    }
                }
                // Leave Stop undisposed: a final lease may race cancellation with this native cleanup.
            }
        }
    }
    private sealed class NativeApi : IDisposable
    {
        private IConnectivityQuery? manager;
        private IConnectionPoint? point;
        private InternetConnectivityCallbackSink? sink;
        private int cookie;
        private bool subscribed;
        internal NativeApi()
        {
            var cls = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"); var iid = typeof(IConnectivityQuery).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref cls, 0, 23, ref iid, out manager));
            try
            {
                var events = typeof(IInternetConnectivityCallback).GUID;
                ((IConnectionPointContainer)manager).FindConnectionPoint(ref events, out point);
            }
            catch { Dispose(); throw; }
        }
        internal void Subscribe(InternetConnectivityCallbackSink callback)
        { sink = callback; point!.Advise(callback, out cookie); subscribed = true; }
        internal uint Read() => unchecked((uint)manager!.GetConnectivity());
        public void Dispose()
        {
            if (subscribed)
            {
                try { point!.Unadvise(cookie); } catch (COMException) { }
                subscribed = false;
            }
            if (point is not null) { Marshal.ReleaseComObject(point); point = null; }
            if (manager is not null) { Marshal.ReleaseComObject(manager); manager = null; }
            sink = null;
        }
    }
    // SDK netlistmgr.idl declares this dual interface's GetConnectivity at dispatch ID 7.
    // IDispatch binding declares only that read method; network inventories and simulated profile setters are unavailable here.
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IConnectivityQuery { [DispId(7)] int GetConnectivity(); }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoCreateInstance(ref Guid cls, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IConnectivityQuery manager);
}
