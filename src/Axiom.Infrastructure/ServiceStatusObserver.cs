using System.Runtime.InteropServices;

namespace Axiom.Infrastructure;

public sealed record ServiceStatusSample(string? State, string? Error);
public sealed record ServiceHandles(IntPtr Manager, IntPtr Service);
public sealed class ServiceNativeException(string stage, int code) : Exception($"{stage}: Win32 {code}")
{
    public string Stage { get; } = stage;
    public int Code { get; } = code;
}

public interface IServiceStatusNative
{
    ServiceHandles Open();
    IntPtr Subscribe(IntPtr service, Action<uint> callback);
    uint Query(IntPtr service);
    void Unsubscribe(IntPtr subscription);
    void Close(ServiceHandles handles);
}

// One physical worker and one native subscription per generation. The callback only marks dirty.
public sealed class ServiceStatusObserver(IServiceStatusNative native)
{
    private readonly object gate = new();
    private Task? worker;
    private AutoResetEvent? wake;
    private volatile bool stopping;
    private long generation;
    public bool Active { get { lock (gate) return worker is { IsCompleted: false }; } }

    public Task StartAsync(Action<ServiceStatusSample> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (gate)
        {
            if (worker is { IsCompleted: false }) throw new InvalidOperationException("Previous native generation is still active.");
            wake?.Dispose();
            wake = new AutoResetEvent(false);
            stopping = false;
            var current = ++generation;
            worker = Task.Factory.StartNew(() => Run(current, publish), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task task;
        AutoResetEvent? signal;
        lock (gate)
        {
            stopping = true;
            wake?.Set();
            task = worker ?? Task.CompletedTask;
            signal = wake;
        }
        await task.ConfigureAwait(false);
        lock (gate)
        {
            if (ReferenceEquals(wake, signal))
            {
                wake = null;
                worker = null;
                signal?.Dispose();
            }
        }
    }

    private void Run(long current, Action<ServiceStatusSample> publish)
    {
        ServiceHandles? handles = null;
        IntPtr registration = IntPtr.Zero;
        var signal = wake!;
        long dirty = 0;
        void Callback(uint _) { if (current == Interlocked.Read(ref generation) && !stopping) { Interlocked.Increment(ref dirty); signal.Set(); } }
        try
        {
            if (stopping) return;
            handles = native.Open();
            if (stopping) return;
            registration = native.Subscribe(handles.Service, Callback);
            if (stopping) return;
            Interlocked.Increment(ref dirty); // baseline after registration
            long queriedDirty = -1;
            while (!stopping)
            {
                var before = Interlocked.Read(ref dirty);
                if (before == queriedDirty) { signal.WaitOne(); continue; }
                try
                {
                    var state = ServiceStateNames.FromNative(native.Query(handles.Service));
                    if (!stopping) publish(state is null
                        ? new(null, "QueryServiceStatusEx: unrecognized service state")
                        : new(state, null));
                }
                catch (Exception exception)
                {
                    if (!stopping) publish(new(null, exception.Message));
                    break; // explicit retry only
                }
                queriedDirty = before;
                if (stopping) break;
            }
        }
        catch (Exception exception)
        {
            if (!stopping) publish(new(null, exception.Message));
        }
        finally
        {
            // Unsubscribe joins native callbacks. Never under the host lock or in a callback.
            try { if (registration != IntPtr.Zero) native.Unsubscribe(registration); }
            finally { if (handles is not null) native.Close(handles); }
            GC.KeepAlive((Action<uint>)Callback);
        }
    }
}

public static class ServiceStateNames
{
    public static string? FromNative(uint value) => value switch
    {
        1 => "stopped", 2 => "start-pending", 3 => "stop-pending", 4 => "running",
        5 => "continue-pending", 6 => "pause-pending", 7 => "paused", _ => null
    };
}

public sealed class WindowsServiceStatusNative : IServiceStatusNative
{
    public const string ServiceName = "EventLog";
    private readonly Dictionary<IntPtr, Callback> callbacks = [];
    private readonly object gate = new();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Callback(uint notification, IntPtr context);

    public ServiceHandles Open()
    {
        var manager = OpenSCManager(null, null, 0x0001); // SC_MANAGER_CONNECT
        if (manager == IntPtr.Zero) throw new ServiceNativeException("OpenSCManagerW", Marshal.GetLastWin32Error());
        var service = OpenService(manager, ServiceName, 0x0004); // SERVICE_QUERY_STATUS
        if (service == IntPtr.Zero)
        {
            var code = Marshal.GetLastWin32Error();
            CloseServiceHandle(manager);
            throw new ServiceNativeException("OpenServiceW", code);
        }
        return new(manager, service);
    }

    public IntPtr Subscribe(IntPtr service, Action<uint> callback)
    {
        Callback rooted = (code, _) => callback(code);
        var code = SubscribeServiceChangeNotifications(service, 2, rooted, IntPtr.Zero, out var registration);
        if (code != 0) throw new ServiceNativeException("SubscribeServiceChangeNotifications", unchecked((int)code));
        lock (gate) callbacks.Add(registration, rooted);
        return registration;
    }

    public uint Query(IntPtr service)
    {
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
            throw new ServiceNativeException("QueryServiceStatusEx", Marshal.GetLastWin32Error());
        return status.CurrentState;
    }

    public void Unsubscribe(IntPtr subscription)
    {
        UnsubscribeServiceChangeNotifications(subscription);
        lock (gate) callbacks.Remove(subscription);
    }

    public void Close(ServiceHandles handles)
    {
        CloseServiceHandle(handles.Service);
        CloseServiceHandle(handles.Manager);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string service, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess status, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("sechost.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint SubscribeServiceChangeNotifications(IntPtr service, int eventType, Callback callback,
        IntPtr context, out IntPtr registration);
    [DllImport("sechost.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern void UnsubscribeServiceChangeNotifications(IntPtr registration);
}
