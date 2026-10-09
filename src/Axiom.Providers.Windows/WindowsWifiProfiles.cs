using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsWifiProfiles
{
    public static async Task ObserveAsync(string[] profiles, Action<WifiProfileValue[], bool> publish, CancellationToken token)
    {
        // Subscribe before querying. Queued hints are re-read from current state, never replayed as old connections.
        using var events = new WindowsWifiEvents();
        Check(WlanOpenHandle(2, 0, out _, out var handle));
        try
        {
            var state = new WifiProfileState(profiles); Refresh();
            var previous = state.Samples(); publish(previous, true);
            await foreach (var hint in events.Events.ReadAllAsync(token))
            {
                token.ThrowIfCancellationRequested();
                if (events.Failed) throw new IOException("Wi-Fi 通知丢失，状态未知。");
                if (hint.Rescan || !state.Contains(hint.Interface)) Refresh();
                else state.Set(Read(hint.Interface));
                var samples = state.Samples();
                if (samples.SequenceEqual(previous)) continue;
                // A recovery from unknown is a fresh baseline: do not execute a connection missed during the gap.
                var baseline = samples.Any(s => s.Connected.HasValue && previous.Any(p => p.ProfileName == s.ProfileName && !p.Connected.HasValue));
                publish(samples, baseline); previous = samples;
            }
            if (!token.IsCancellationRequested) throw new IOException("Wi-Fi 通知已结束。");

            void Refresh()
            {
                token.ThrowIfCancellationRequested();
                var error = WlanEnumInterfaces(handle, 0, out var list);
                if (error != 0) { state.Replace([]); return; }
                try
                {
                    var count = Marshal.ReadInt32(list);
                    if (count is < 0 or > 64) throw new IOException("Wi-Fi 网卡数量超出检测范围。");
                    var values = new List<WifiInterfaceValue>();
                    // WLAN_INTERFACE_INFO: GUID, WCHAR[256] description, WLAN_INTERFACE_STATE.
                    for (var i = 0; i < count; i++) values.Add(Read(Marshal.PtrToStructure<Guid>(list + 8 + i * 532)));
                    state.Replace(values);
                }
                finally { WlanFreeMemory(list); }
            }
            WifiInterfaceValue Read(Guid id)
            {
                token.ThrowIfCancellationRequested();
                var error = WlanQueryInterface(handle, ref id, 6, 0, out var size, out var value, out _);
                if (error != 0) return new(id, false, null);
                uint connection;
                try
                {
                    if (size < 4) return new(id, false, null);
                    connection = unchecked((uint)Marshal.ReadInt32(value));
                }
                finally { WlanFreeMemory(value); }
                var connected = WifiProfileState.ConnectionStatus(connection);
                if (connected != true) return new(id, connected.HasValue, null);
                error = WlanQueryInterface(handle, ref id, 7, 0, out size, out value, out _);
                if (error != 0) return new(id, false, null);
                try
                {
                    // Read only WLAN_CONNECTION_ATTRIBUTES' state and bounded profile name. Ignore SSID/security payloads.
                    if (size < 520 || Marshal.ReadInt32(value) != 1) return new(id, false, null);
                    var chars = new char[256]; Marshal.Copy(value + 8, chars, 0, chars.Length);
                    var end = Array.IndexOf(chars, '\0');
                    if (end < 1 || chars.Take(end).Any(char.IsControl)) return new(id, false, null);
                    return new(id, true, new string(chars, 0, end));
                }
                finally { WlanFreeMemory(value); }
            }
        }
        finally { WlanCloseHandle(handle, 0); }
    }
    internal static void Check(uint result) { if (result != 0) throw new Win32Exception(unchecked((int)result), "Windows Wi-Fi 服务不可用。"); }
    [DllImport("wlanapi.dll")] internal static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll")] internal static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(nint handle, ref Guid id, uint opcode, nint reserved, out uint size, out nint data, out uint valueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(nint memory);
}

internal readonly record struct WifiNotification(Guid Interface, bool Rescan);

[SupportedOSPlatform("windows")]
internal sealed class WindowsWifiEvents : IDisposable
{
    private static readonly object Gate = new();
    private static Hub? shared;
    private readonly Channel<WifiNotification> queue = Channel.CreateBounded<WifiNotification>(64);
    private readonly Hub owner;
    private bool disposed;
    private volatile bool failed;
    internal ChannelReader<WifiNotification> Events => queue.Reader;
    internal bool Failed => failed;
    public static int ActiveConsumers { get { lock (Gate) return shared?.Clients.Count ?? 0; } }
    internal WindowsWifiEvents()
    {
        lock (Gate)
        {
            if (shared is { Closing: true }) throw new IOException("Wi-Fi 通知仍在结束，请稍后重新启用。");
            shared ??= new Hub(); owner = shared; owner.Clients.Add(this);
        }
        try
        {
            owner.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (owner.Stopped.Task.IsCompleted) throw new IOException("Wi-Fi 通知已停止。");
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        var last = false;
        lock (Gate)
        {
            if (disposed) return; disposed = true; queue.Writer.TryComplete(); owner.Clients.Remove(this);
            if (owner.Clients.Count == 0 && !owner.Stopped.Task.IsCompleted) { last = true; owner.Closing = true; owner.Stop.Set(); }
        }
        // Unregistration is outside Gate: Windows may wait for a callback which also needs Gate.
        if (last) owner.Stopped.Task.Wait(TimeSpan.FromSeconds(5));
    }
    private sealed class Hub
    {
        internal readonly HashSet<WindowsWifiEvents> Clients = [];
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Stop = new();
        internal bool Closing;
        internal Hub() => new Thread(Run) { IsBackground = true, Name = "Axiom Wi-Fi events" }.Start();
        private void Notify(ref NotificationData data, nint context)
        {
            // ACM only; no profile/SSID/device description is read from callback memory.
            if (data.Source != 8 || data.Code is not (1 or 2 or 6 or 10 or 13 or 14 or 15 or 16 or 21 or 22 or 27)) return;
            var hint = new WifiNotification(data.Interface, data.Code is not (10 or 21));
            lock (Gate) foreach (var client in Clients)
                if (!client.queue.Writer.TryWrite(hint))
                { client.failed = true; client.queue.Writer.TryComplete(new IOException("Wi-Fi 通知队列已满，状态未知。")); }
        }
        private void Run()
        {
            nint handle = 0; var registered = false; NotificationCallback callback = Notify;
            try
            {
                WindowsWifiProfiles.Check(WindowsWifiProfiles.WlanOpenHandle(2, 0, out _, out handle));
                WindowsWifiProfiles.Check(WlanRegisterNotification(handle, 8, false, callback, 0, 0, out _)); registered = true;
                Ready.TrySetResult(); Stop.Wait();
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                var failure = new IOException("Windows 原生 Wi-Fi 通知不可用。", error); Ready.TrySetException(failure);
                lock (Gate) foreach (var client in Clients) { client.failed = true; client.queue.Writer.TryComplete(failure); }
            }
            finally
            {
                if (registered) WlanRegisterNotification(handle, 0, false, null, 0, 0, out _);
                if (handle != 0) WindowsWifiProfiles.WlanCloseHandle(handle, 0);
                GC.KeepAlive(callback);
                lock (Gate)
                {
                    foreach (var client in Clients) { client.failed = true; client.queue.Writer.TryComplete(new IOException("Wi-Fi 通知已停止。")); }
                    if (ReferenceEquals(shared, this)) shared = null;
                    Stopped.TrySetResult(); Stop.Dispose();
                }
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NotificationData { public uint Source; public uint Code; public Guid Interface; public uint Size; public nint Data; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void NotificationCallback(ref NotificationData data, nint context);
    [DllImport("wlanapi.dll")] private static extern uint WlanRegisterNotification(nint handle, uint source, [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate,
        NotificationCallback? callback, nint context, nint reserved, out uint previousSource);
}
