using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsAudioPlayback
{
    internal static IApplicationAudioActivityApi CreateActivityApi(Action<ApplicationAudioHint> notify)
    {
        var api = new ActivityApi(notify);
        try { api.Initialize(); return api; }
        catch { api.Dispose(); throw; }
    }
    private sealed class ActivityApi(Action<ApplicationAudioHint> notify) : IApplicationAudioActivityApi
    {
        private IDeviceEnumerator? enumerator;
        private IDevice? device;
        private object? managerObject;
        private readonly Dictionary<string, ActivitySession> sessions = new(StringComparer.Ordinal);
        private WindowsAudioEvents.NotificationSink? defaults;
        private ActivityCreatedSink? created;
        private nint defaultsPointer, createdPointer;
        private bool initialized, defaultRegistered, sessionRegistered;
        private long bound = -1;
        private bool available, enumerated;
        private string? endpointId;
        private string sid = ""; private int userSession;
        private IAudioSessionManager2 Manager => (IAudioSessionManager2)managerObject!;
        internal void Initialize()
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0)); initialized = true;
            using var identity = WindowsIdentity.GetCurrent(); sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
            using var process = Process.GetCurrentProcess(); userSession = process.SessionId;
            if (userSession <= 0 || !Environment.UserInteractive) throw new UnauthorizedAccessException();
            var cls = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"); var iid = typeof(IDeviceEnumerator).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref cls, 0, 1, ref iid, out enumerator));
            defaults = new(endpoint => { if (endpoint == "output") notify(new("default")); }, () => notify(new("inventory")));
            defaultsPointer = Marshal.GetComInterfaceForObject(defaults, typeof(IAudioNotificationClient));
            defaultRegistered = true; Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(defaultsPointer));
        }
        public ApplicationAudioReading[] Read(ApplicationAudioHint hint, long selection, string[] paths)
        {
            if (!available || bound != selection || hint.Kind is "sync" or "inventory") available = Bind(selection);
            if (!available)
            {
                if (hint.Created is not null) throw new IOException("新会话通知无法接入，检测未知。");
                return [new(null, null)];
            }
            if (hint.Created is ActivityControlPointer pointer)
            {
                var control = Marshal.GetObjectForIUnknown(pointer.Pointer);
                Add(control, selection);
            }
            if (!enumerated) Enumerate(selection);
            // Expired is terminal; inactive sessions remain subscribed because they can resume.
            // Check the supplied control again on Add so late created callbacks cannot retain expired entries.
            foreach (var pair in sessions.ToArray())
                if (pair.Value.Expired) { sessions.Remove(pair.Key); pair.Value.Dispose(); }
            return [.. sessions.Values.Select(session => session.Read(paths)), .. enumerated ? Array.Empty<ApplicationAudioReading>() : [new ApplicationAudioReading(null, null)]];
        }
        private bool Bind(long selection)
        {
            IDevice? candidate = null;
            try
            {
                if (enumerator!.GetDefaultAudioEndpoint(0, 0, out candidate) < 0 || candidate is null || candidate.GetState(out var state) < 0 || state != 1) return false;
                var idResult = candidate.GetId(out var id); string identity;
                try { if (idResult < 0) return false; identity = ReadString(id); } catch (IOException) { return false; } finally { if (id != 0) Marshal.FreeCoTaskMem(id); }
                if (managerObject is not null && bound == selection)
                {
                    if (endpointId == identity) return true;
                    notify(new("default")); return false; // An inventory-detected selection change also gets a new baseline.
                }
                ReleaseBinding(); bound = selection; endpointId = identity;
                device = candidate; candidate = null;
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, 23, 0, out managerObject) < 0 || managerObject is null) { ReleaseBinding(); return false; }
                created = new(notify, selection);
                createdPointer = Marshal.GetComInterfaceForObject(created, typeof(IApplicationAudioSessionNotification));
                sessionRegistered = true; Marshal.ThrowExceptionForHR(Manager.RegisterSessionNotification(createdPointer));
                // Register first, then GetCount enables delivery. Keep the notification-provided controls;
                // Microsoft documents that an enumerator alone can omit newly notified sessions.
                Enumerate(selection); return true;
            }
            finally { if (candidate is not null) Marshal.ReleaseComObject(candidate); }
        }
        private void Enumerate(long selection)
        {
            IAudioSessionEnumerator? list = null; enumerated = false;
            try
            {
                if (Manager.GetSessionEnumerator(out list) < 0 || list.GetCount(out var count) < 0) return;
                if (count is < 0 or > 128) throw new IOException("应用音频会话数量超出128上限。");
                for (var i = 0; i < count; i++)
                {
                    if (list.GetSession(i, out var control) < 0) { if (control is not null) Marshal.ReleaseComObject(control); return; }
                    Add(control, selection);
                }
                enumerated = true;
            }
            finally { if (list is not null) Marshal.ReleaseComObject(list); }
        }
        private void Add(object control, long selection)
        {
            var taken = false;
            try
            {
                if (((IAudioSessionControl2)control).GetState(out var state) >= 0 && state == 2) return;
                Marshal.ThrowExceptionForHR(((IAudioSessionControl2)control).GetSessionInstanceIdentifier(out var id)); string key;
                try { key = ReadString(id); } finally { if (id != 0) Marshal.FreeCoTaskMem(id); }
                if (sessions.ContainsKey(key)) return;
                if (sessions.Count >= 128) throw new IOException("应用音频会话容量已满，检测未知。");
                var entry = new ActivitySession(control, sid, userSession, notify, selection); taken = true;
                try { entry.Register(); sessions.Add(key, entry); }
                catch { entry.Dispose(); throw; }
            }
            finally { if (!taken) Marshal.ReleaseComObject(control); }
        }
        private void ReleaseBinding()
        {
            enumerated = false; Exception? failure = null;
            void Release(Action action)
            {
                try { action(); }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { failure ??= error; }
            }
            foreach (var session in sessions.Values) Release(session.Dispose); sessions.Clear();
            if (sessionRegistered) { sessionRegistered = false; Release(() => Manager.UnregisterSessionNotification(createdPointer)); }
            if (createdPointer != 0) { var pointer = createdPointer; createdPointer = 0; Release(() => Marshal.Release(pointer)); } created = null;
            if (managerObject is not null) { var value = managerObject; managerObject = null; Release(() => Marshal.ReleaseComObject(value)); }
            if (device is not null) { var value = device; device = null; Release(() => Marshal.ReleaseComObject(value)); }
            if (failure is not null) throw new IOException("应用音频订阅清理失败。", failure);
        }
        public void Dispose()
        {
            try { ReleaseBinding(); }
            finally
            {
                try { if (defaultRegistered) { defaultRegistered = false; enumerator!.UnregisterEndpointNotificationCallback(defaultsPointer); } }
                finally
                {
                    if (defaultsPointer != 0) { Marshal.Release(defaultsPointer); defaultsPointer = 0; } defaults = null;
                    if (enumerator is not null) { Marshal.ReleaseComObject(enumerator); enumerator = null; }
                    if (initialized) { initialized = false; CoUninitialize(); }
                }
            }
        }
        private static string ReadString(nint pointer)
        {
            if (pointer != 0)
                for (var length = 0; length < 4096; length++)
                    if (Marshal.ReadInt16(pointer, length * 2) == 0) return length > 0 ? Marshal.PtrToStringUni(pointer, length)! : throw new IOException("音频身份为空。");
            throw new IOException("音频会话或端点身份不可用。");
        }
    }
    private sealed class ActivitySession : IDisposable, IApplicationAudioActivitySession
    {
        private readonly Session identity;
        private readonly IAudioSessionControl2 control;
        private readonly Action<ApplicationAudioHint> notify;
        private readonly long generation;
        private readonly ActivityStateSink sink;
        private nint sinkPointer;
        private bool registered;
        private WaitHandle? process;
        private RegisteredWaitHandle? wait;
        private string? path;
        internal ActivitySession(object value, string sid, int session, Action<ApplicationAudioHint> notify, long generation)
        {
            identity = new(value, sid, session, CancellationToken.None); control = (IAudioSessionControl2)value;
            this.notify = notify; this.generation = generation;
            sink = new(() => notify(new("read", generation)), () => notify(new("failed", generation)));
        }
        internal void Register()
        {
            sinkPointer = Marshal.GetComInterfaceForObject(sink, typeof(IApplicationAudioSessionEvents));
            registered = true; Marshal.ThrowExceptionForHR(control.RegisterAudioSessionNotification(sinkPointer));
        }
        internal ApplicationAudioReading Read(string[] paths)
            => ApplicationAudioSessionReading.Read(this, paths, path);
        internal bool Expired => control.GetState(out var state) >= 0 && state == 2;
        public bool ProcessExited => identity.ProcessExited;
        public int State { get { Marshal.ThrowExceptionForHR(control.GetState(out var state)); return state; } }
        public AudioSessionIdentity Identify(out string? candidate)
        { var result = identity.Identify(out candidate); if (result == AudioSessionIdentity.Owned) path = candidate; return result; }
        public void WatchExit()
        {
            if (wait is not null) return;
            process = identity.BorrowProcessWait() ?? throw new IOException("播放进程句柄不可用。");
            wait = ThreadPool.RegisterWaitForSingleObject(process, (_, _) => notify(new("exit", generation)), null, Timeout.Infinite, executeOnlyOnce: true);
        }
        public void Dispose()
        {
            try
            {
                if (wait is not null)
                {
                    using var finished = new ManualResetEvent(false);
                    if (wait.Unregister(finished)) finished.WaitOne(); wait = null;
                }
                process?.Dispose(); process = null;
                if (registered) { registered = false; control.UnregisterAudioSessionNotification(sinkPointer); }
            }
            finally { if (sinkPointer != 0) { Marshal.Release(sinkPointer); sinkPointer = 0; } identity.Dispose(); }
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ActivityControlPointer(nint pointer) : IDisposable
{
    internal nint Pointer => pointer;
    private bool disposed;
    public void Dispose() { if (!disposed) { disposed = true; Marshal.Release(pointer); } }
}
[SupportedOSPlatform("windows"), ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class ActivityCreatedSink(Action<ApplicationAudioHint> notify, long generation) : IApplicationAudioSessionNotification
{
    public int OnSessionCreated(nint control)
    {
        if (control == 0) { notify(new("failed", generation)); return 0; }
        Marshal.AddRef(control); notify(new("created", generation, new ActivityControlPointer(control))); return 0;
    }
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class ActivityStateSink(Action changed, Action failed) : IApplicationAudioSessionEvents
{
    public int OnDisplayNameChanged(nint name, nint context) => 0;
    public int OnIconPathChanged(nint path, nint context) => 0;
    public int OnSimpleVolumeChanged(float volume, bool mute, nint context) => 0;
    public int OnChannelVolumeChanged(uint count, nint volumes, uint changedChannel, nint context) => 0;
    public int OnGroupingParamChanged(nint group, nint context) => 0;
    public int OnStateChanged(int state) { changed(); return 0; }
    public int OnSessionDisconnected(int reason) { failed(); return 0; }
}

// CCWs expose the documented callback interfaces; payload strings, volume arrays and content are not read.
[ComVisible(true), Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IApplicationAudioSessionNotification { [PreserveSig] int OnSessionCreated(nint control); }
[ComVisible(true), Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IApplicationAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged(nint name, nint context);
    [PreserveSig] int OnIconPathChanged(nint path, nint context);
    [PreserveSig] int OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool mute, nint context);
    [PreserveSig] int OnChannelVolumeChanged(uint count, nint volumes, uint changedChannel, nint context);
    [PreserveSig] int OnGroupingParamChanged(nint group, nint context);
    [PreserveSig] int OnStateChanged(int state);
    [PreserveSig] int OnSessionDisconnected(int reason);
}
