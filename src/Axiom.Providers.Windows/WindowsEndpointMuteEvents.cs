using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsEndpointMuteEvents : IEndpointMuteSource
{
    private static readonly object Gate = new();
    private static Hub? shared;
    private readonly Hub owner;
    private readonly HashSet<string> endpoints;
    private readonly Channel<EndpointMuteSample> queue = Channel.CreateBounded<EndpointMuteSample>(64);
    private volatile bool failed;
    private bool disposed;
    public ChannelReader<EndpointMuteSample> Events => queue.Reader;
    public bool Failed => failed;
    public static int ActiveConsumers { get { lock (Gate) return shared?.Clients.Count ?? 0; } }
    public WindowsEndpointMuteEvents(string[] endpoints)
    {
        if (endpoints.Length is < 1 or > 2 || endpoints.Any(e => e is not ("input" or "output")) || endpoints.Distinct().Count() != endpoints.Length)
            throw new ArgumentException("默认设备静音订阅无效。");
        this.endpoints = new(endpoints, StringComparer.Ordinal);
        lock (Gate)
        {
            if (shared is { Closing: true }) throw new IOException("静音通知仍在结束，请稍后重新连接。");
            shared ??= new Hub(); owner = shared; owner.Clients.Add(this);
            foreach (var endpoint in endpoints)
                queue.Writer.TryWrite(owner.Latest.TryGetValue(endpoint, out var sample) ? sample with { Baseline = true } :
                    new(endpoint, null, true, "正在连接默认设备静音通知。"));
        }
        owner.Enqueue(new("sync")); // Construction never waits for native COM activation.
    }
    public void Dispose()
    {
        var last = false;
        lock (Gate)
        {
            if (disposed) return; disposed = true; queue.Writer.TryComplete(); owner.Clients.Remove(this);
            if (owner.Clients.Count == 0 && !owner.Stopped.Task.IsCompleted)
            { last = true; owner.Closing = true; owner.Work.Complete(); }
        }
        if (last) owner.Stopped.Task.Wait(TimeSpan.FromSeconds(5));
        else owner.Enqueue(new("sync"));
    }
    private sealed class Hub
    {
        internal readonly HashSet<WindowsEndpointMuteEvents> Clients = [];
        internal readonly Dictionary<string, EndpointMuteSample> Latest = new(StringComparer.Ordinal);
        internal readonly EndpointMuteNotifications Work;
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closing;
        private readonly Dictionary<string, EndpointMuteState> states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Binding> bindings = new(StringComparer.Ordinal);
        private readonly HashSet<string> active = new(StringComparer.Ordinal);
        internal Hub()
        {
            Work = new(CompleteLostNotifications);
            var thread = new Thread(Run) { IsBackground = true, Name = "Axiom endpoint mute observer" };
            thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        }
        internal void Enqueue(EndpointMuteSignal signal) => Work.Enqueue(signal);
        private void CompleteLostNotifications()
        {
            lock (Gate)
            {
                Closing = true;
                foreach (var client in Clients)
                { client.failed = true; client.queue.Writer.TryComplete(new IOException("静音状态通知丢失。")); }
            }
        }
        private void Publish(EndpointMuteSample sample)
        {
            lock (Gate)
            {
                Latest[sample.Endpoint] = sample;
                foreach (var client in Clients.Where(c => c.endpoints.Contains(sample.Endpoint)))
                    if (!client.queue.Writer.TryWrite(sample))
                    { client.failed = true; client.queue.Writer.TryComplete(new IOException("静音通知积压，状态未知。")); }
            }
        }
        private void Run()
        {
            IDeviceEnumerator? enumerator = null; WindowsAudioEvents? defaults = null;
            using var stop = new CancellationTokenSource(); var initialized = false;
            try
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0)); initialized = true;
                var cls = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"); var iid = typeof(IDeviceEnumerator).GUID;
                Marshal.ThrowExceptionForHR(CoCreateInstance(ref cls, 0, 1, ref iid, out enumerator));
                defaults = new WindowsAudioEvents(includeEndpointState: true);
                _ = ForwardDefaultsAsync(defaults, stop.Token);
                while (Work.Events.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    if (Work.Failed) throw new IOException("静音通知队列不可用。");
                    if (!Work.TryRead(out var signal)) continue;
                    lock (Gate) if (Closing) break;
                    switch (signal!.Kind)
                    {
                        case "sync": Synchronize(enumerator); break;
                        case "default":
                            foreach (var endpoint in active.Where(e => signal.Endpoint == "endpoints" || e == signal.Endpoint).ToArray())
                                BindDefault(enumerator, endpoint);
                            break;
                        case "read": ReadMute(signal); break;
                        case "unavailable": throw new IOException("默认设备通知中断。");
                    }
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                CompleteLostNotifications();
                foreach (var endpoint in states.Keys.ToArray()) Publish(states[endpoint].Reset("默认设备静音通知不可用；请暂停并重新启用规则。"));
                lock (Gate) foreach (var client in Clients) client.queue.Writer.TryComplete(new IOException("静音通知不可用。"));
            }
            finally
            {
                stop.Cancel(); defaults?.Dispose();
                foreach (var binding in bindings.Values) binding.Dispose(); bindings.Clear();
                if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
                if (initialized) CoUninitialize();
                lock (Gate)
                {
                    Closing = true;
                    foreach (var client in Clients) client.queue.Writer.TryComplete();
                    if (ReferenceEquals(shared, this)) shared = null;
                    Stopped.TrySetResult();
                }
            }
        }
        private async Task ForwardDefaultsAsync(WindowsAudioEvents defaults, CancellationToken token)
        {
            try
            {
                await foreach (var endpoint in defaults.Events.ReadAllAsync(token)) Enqueue(new("default", endpoint));
                if (!token.IsCancellationRequested) Enqueue(new("unavailable"));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { Enqueue(new("unavailable")); }
        }
        private void Synchronize(IDeviceEnumerator enumerator)
        {
            HashSet<string> wanted;
            lock (Gate) wanted = new(Clients.SelectMany(c => c.endpoints), StringComparer.Ordinal);
            foreach (var endpoint in active.Except(wanted).ToArray())
            {
                if (bindings.Remove(endpoint, out var binding)) binding.Dispose();
                states[endpoint].Reset("静音检测已取消。"); active.Remove(endpoint);
                lock (Gate) Latest.Remove(endpoint);
            }
            foreach (var endpoint in wanted)
            {
                if (!states.ContainsKey(endpoint)) states[endpoint] = new(endpoint);
                active.Add(endpoint);
                BindDefault(enumerator, endpoint);
            }
        }
        private void BindDefault(IDeviceEnumerator enumerator, string endpoint)
        {
            IDevice? device = null; object? activated = null; Binding? created = null;
            var state = states[endpoint];
            long selectionVersion;
            lock (Work.Gate) selectionVersion = Work.DefaultChanges.Version(endpoint);
            try
            {
                if (bindings.TryGetValue(endpoint, out var previous) && previous.SelectionVersion != selectionVersion)
                {
                    Publish(state.Reset("默认设备正在重新连接。"));
                    bindings.Remove(endpoint); previous.Dispose();
                }
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(endpoint == "input" ? 1 : 0, endpoint == "input" ? 2 : 0, out device));
                Marshal.ThrowExceptionForHR(device.GetState(out var status));
                if (status != 1) throw new IOException("默认设备未处于活动状态。");
                var identity = ReadIdentity(device); // Transient comparison only; never publish, log, or persist this string.
                bool querySuperseded;
                lock (Work.Gate) querySuperseded = Work.Failed || selectionVersion != Work.DefaultChanges.Version(endpoint);
                if (querySuperseded)
                {
                    Publish(state.Reset("默认设备正在重新连接。"));
                    if (bindings.Remove(endpoint, out var superseded)) superseded.Dispose();
                    return; // The already queued role notification will query the current default again.
                }
                if (bindings.TryGetValue(endpoint, out var current) &&
                    !EndpointMuteDefaultChanges.RequiresRebind(current.Identity, identity, current.SelectionVersion, selectionVersion))
                {
                    // An unrelated inventory event retains the Known mute state while validating this binding.
                    ReadMute(new("read", endpoint, state.Generation)); return;
                }
                Publish(state.Reset("默认设备正在重新连接。"));
                if (bindings.Remove(endpoint, out var old)) old.Dispose();
                var iid = typeof(IEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out activated));
                // Capture the generation, not the mutable state, in this endpoint's callback.
                var generation = state.Generation;
                var sink = new EndpointMuteCallbackSink(() => Enqueue(new("read", endpoint, generation)));
                created = new(device, activated, identity, selectionVersion, sink); device = null; activated = null;
                created.Register();
                Marshal.ThrowExceptionForHR(created.Volume.GetMute(out var muted));
                bindings[endpoint] = created; created = null;
                EndpointMuteSample? sample;
                lock (Work.Gate)
                {
                    if (Work.Failed || selectionVersion != Work.DefaultChanges.Version(endpoint)) return;
                    sample = state.Read(generation, muted);
                }
                if (sample is not null) Publish(sample);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                if (bindings.Remove(endpoint, out var old)) old.Dispose();
                Publish(state.Reset("默认设备不可用或静音状态无法读取。"));
            }
            finally
            {
                created?.Dispose();
                if (activated is not null) Marshal.ReleaseComObject(activated);
                if (device is not null) Marshal.ReleaseComObject(device);
            }
        }
        private void ReadMute(EndpointMuteSignal signal)
        {
            if (!states.TryGetValue(signal.Endpoint, out var state) || state.Generation != signal.Generation || !bindings.TryGetValue(signal.Endpoint, out var binding)) return;
            // A queued default selection invalidates old callbacks even before its coalesced rebind signal runs.
            lock (Work.Gate) if (Work.Failed || binding.SelectionVersion != Work.DefaultChanges.Version(signal.Endpoint)) return;
            try
            {
                Marshal.ThrowExceptionForHR(binding.Volume.GetMute(out var muted));
                EndpointMuteSample? sample;
                lock (Work.Gate)
                {
                    if (Work.Failed || binding.SelectionVersion != Work.DefaultChanges.Version(signal.Endpoint)) return;
                    sample = state.Read(signal.Generation, muted);
                }
                if (sample is not null) Publish(sample);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                bindings.Remove(signal.Endpoint); binding.Dispose();
                Publish(state.Reset("默认设备静音状态无法读取；等待新的设备通知或重新启用规则。"));
            }
        }
    }
    private sealed class Binding(IDevice device, object activated, string identity, long selectionVersion, EndpointMuteCallbackSink sink) : IDisposable
    {
        internal readonly IEndpointVolume Volume = (IEndpointVolume)activated;
        internal readonly string Identity = identity;
        internal readonly long SelectionVersion = selectionVersion;
        private bool registered;
        internal void Register() { registered = true; Marshal.ThrowExceptionForHR(Volume.RegisterControlChangeNotify(sink)); }
        public void Dispose()
        {
            if (registered)
            {
                try { Volume.UnregisterControlChangeNotify(sink); } catch (COMException) { }
                registered = false;
            }
            Marshal.ReleaseComObject(activated); Marshal.ReleaseComObject(device);
        }
    }
    private static string ReadIdentity(IDevice device)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var pointer));
        try
        {
            if (pointer == 0) throw new IOException("默认设备身份不可用。");
            for (var length = 0; length < 4096; length++)
                if (Marshal.ReadInt16(pointer, length * 2) == 0)
                    return length > 0 ? Marshal.PtrToStringUni(pointer, length)! : throw new IOException("默认设备身份不可用。");
            throw new IOException("默认设备身份长度异常。");
        }
        finally { if (pointer != 0) Marshal.FreeCoTaskMem(pointer); }
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, [MarshalAs(UnmanagedType.Interface)] out IDevice device);
        [PreserveSig] int GetDevice(nint id, out nint device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint mode, out nint properties);
        [PreserveSig] int GetId(out nint id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IEndpointMuteCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IEndpointMuteCallback notify);
        [PreserveSig] int GetChannelCount(out uint channels);
        [PreserveSig] int SetMasterVolumeLevel(float value, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float value, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float value);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float value);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float value, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float value, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float value);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float value);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid cls, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IDeviceEnumerator instance);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class EndpointMuteCallbackSink(Action notify) : IEndpointMuteCallback
{
    // AUDIO_VOLUME_NOTIFICATION_DATA is deliberately not dereferenced. Read GetMute on the MTA worker.
    public int OnNotify(nint data) { notify(); return 0; }
}

// A public COM-visible interface is required for the CCW queried by IAudioEndpointVolume.
[ComVisible(true), Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEndpointMuteCallback
{
    [PreserveSig] int OnNotify(nint notificationData);
}
