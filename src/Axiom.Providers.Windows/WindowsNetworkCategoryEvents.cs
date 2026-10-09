using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkCategoryEvents : INetworkCategorySource
{
    private static readonly object Gate = new();
    private static Hub? shared;
    private readonly Hub owner;
    private readonly Channel<NetworkCategorySample> queue = Channel.CreateBounded<NetworkCategorySample>(64);
    private volatile bool failed;
    private bool disposed;
    public ChannelReader<NetworkCategorySample> Events => queue.Reader;
    public bool Failed => failed;
    public static int ActiveConsumers { get { lock (Gate) return shared?.Clients.Count ?? 0; } }
    public WindowsNetworkCategoryEvents()
    {
        lock (Gate)
        {
            if (shared is { Closing: true }) throw new IOException("网络分类通知仍在结束，请稍后重新启用规则。");
            shared ??= new Hub(); owner = shared; owner.Clients.Add(this);
            queue.Writer.TryWrite(owner.Latest with { Baselines = [true, true, true] });
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
        internal readonly HashSet<WindowsNetworkCategoryEvents> Clients = [];
        internal readonly NetworkCategoryNotifications Hints;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal NetworkCategorySample Latest = new([null, null, null], [true, true, true], "正在连接Windows网络分类通知。");
        internal bool Closing;
        internal Hub()
        {
            Hints = new(CompleteLostNotifications);
            var thread = new Thread(Run) { IsBackground = true, Name = "Axiom Windows network category" };
            thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        }
        private void CompleteLostNotifications()
        {
            lock (Gate)
            {
                Closing = true;
                foreach (var client in Clients)
                { client.failed = true; client.queue.Writer.TryComplete(new IOException("网络分类状态通知丢失。")); }
            }
        }
        private void Publish(NetworkCategorySample sample)
        {
            lock (Gate)
            {
                Latest = sample;
                foreach (var client in Clients)
                    if (!client.queue.Writer.TryWrite(sample))
                    { client.failed = true; client.queue.Writer.TryComplete(new IOException("网络分类状态样本积压，检测状态未知。")); }
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
                native.Subscribe(new NetworkCategoryCallbackSink(Hints.Hint));
                NetworkCategoryObservation.Run(Hints, native.Read, Publish, Stop.Token);
            }
            catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                lock (Gate) { Closing = true; foreach (var client in Clients) client.failed = true; }
                Publish(new([null, null, null], [false, false, false], "Windows 网络分类状态服务、查询或通知不可用；请检查服务后暂停并重新启用规则。"));
                lock (Gate) foreach (var client in Clients) client.queue.Writer.TryComplete(new IOException("Windows 网络分类状态通知中断。"));
            }
            finally
            {
                try { native?.Dispose(); }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
                { CompleteLostNotifications(); }
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
        private ICategoryManager? manager;
        private IConnectionPoint? point;
        private NetworkCategoryCallbackSink? sink;
        private int cookie;
        private bool subscribed;
        internal NativeApi()
        {
            var cls = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"); var iid = typeof(ICategoryManager).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref cls, 0, 23, ref iid, out manager));
            try
            {
                var events = typeof(INetworkCategoryCallback).GUID;
                ((IConnectionPointContainer)manager).FindConnectionPoint(ref events, out point);
            }
            catch { Dispose(); throw; }
        }
        internal void Subscribe(NetworkCategoryCallbackSink callback)
        { sink = callback; point!.Advise(callback, out cookie); subscribed = true; }
        internal NetworkCategorySnapshot Read()
        {
            var categories = new List<int?>(); ICategoryCollection? collection = null; IEnumVARIANT? enumerator = null;
            try
            {
                collection = manager!.GetNetworks(1); // NLM_ENUM_NETWORK_CONNECTED, never request disconnected network history.
                enumerator = collection.Enumerator;
                var item = new object[1];
                for (var count = 0; count <= 64; count++)
                {
                    item[0] = null!; var hr = enumerator.Next(1, item, 0);
                    if (item[0] is null)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                        return new([.. categories], hr == 1);
                    }
                    try
                    {
                        Marshal.ThrowExceptionForHR(hr);
                        if (hr != 0) return new([.. categories], false);
                        if (count == 64) return new([.. categories], false);
                        try
                        {
                            var network = (ICategoryNetwork)item[0];
                            if (network.IsConnected)
                            {
                                var value = network.GetCategory(); categories.Add(value is >= 0 and <= 2 ? value : null);
                            }
                        }
                        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { categories.Add(null); }
                    }
                    finally { Marshal.ReleaseComObject(item[0]); }
                }
                return new([.. categories], false);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return new([.. categories], false); }
            finally
            {
                if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
                if (collection is not null) Marshal.ReleaseComObject(collection);
            }
        }
        public void Dispose()
        {
            if (subscribed) { try { point!.Unadvise(cookie); } catch (COMException) { } subscribed = false; }
            if (point is not null) { Marshal.ReleaseComObject(point); point = null; }
            if (manager is not null) { Marshal.ReleaseComObject(manager); manager = null; }
            sink = null;
        }
    }
    // Public SDK dual interfaces, narrowed to the only three read operations used here.
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface ICategoryManager
    { [DispId(1)] [return: MarshalAs(UnmanagedType.Interface)] ICategoryCollection GetNetworks(int flags); }
    [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface ICategoryCollection
    { [DispId(-4)] IEnumVARIANT Enumerator { [return: MarshalAs(UnmanagedType.Interface)] get; } }
    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface ICategoryNetwork
    {
        [DispId(10)] bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        [DispId(12)] int GetCategory();
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoCreateInstance(ref Guid cls, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out ICategoryManager manager);
}
