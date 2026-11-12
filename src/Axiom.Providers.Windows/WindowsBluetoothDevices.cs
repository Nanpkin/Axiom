using System.Runtime.Versioning;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows10.0.10586.0")]
public static class WindowsBluetoothDevices
{
    internal const string ConnectedProperty = "System.Devices.Aep.IsConnected";
    // Paired endpoints only: no inquiry, input access, pairing, or HID/USB enumeration.
    // System selectors include IssueInquiry=False. A handwritten IsPaired filter alone still triggers discovery.
    internal static string Selector => $"({BluetoothDevice.GetDeviceSelectorFromPairingState(true)}) OR ({BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)})";

    public static async Task ObserveAsync(string[] names, Action<BluetoothDeviceValue[], bool> publish, CancellationToken token)
    {
        using var system = new WindowsSessionEvents();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(token);
            var observation = ObserveCycleAsync(names, publish, cycle.Token);
            var resume = WaitForResumeAsync(system, cycle.Token);
            try
            {
                var finished = await Task.WhenAny(observation, resume);
                await finished;
                if (ReferenceEquals(finished, observation)) throw new IOException("蓝牙连接状态订阅已停止。");
            }
            finally
            {
                cycle.Cancel();
                try { await Task.WhenAll(observation, resume); } catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
            }
            // Re-enumerate after resume; missed sleep edges must not execute commands.
            publish([.. names.Select(n => new BluetoothDeviceValue(n, null))], true);
        }
    }

    private static async Task WaitForResumeAsync(WindowsSessionEvents system, CancellationToken token)
    {
        await foreach (var kind in system.Events.ReadAllAsync(token))
        {
            if (system.Failed) throw new IOException("系统恢复通知不可用。");
            if (kind == "resumed") return;
        }
        throw new IOException("系统恢复通知已停止。");
    }

    private static async Task ObserveCycleAsync(string[] names, Action<BluetoothDeviceValue[], bool> publish, CancellationToken token)
    {
        var queue = Channel.CreateBounded<BluetoothDeviceChange>(256);
        var gate = new object(); var failed = false; var disposed = false;
        var devices = new Dictionary<string, DeviceInformation>(StringComparer.Ordinal);
        var watcher = DeviceInformation.CreateWatcher(Selector, [ConnectedProperty], DeviceInformationKind.AssociationEndpoint);
        void Fail()
        {
            failed = true;
            queue.Writer.TryComplete(new IOException("Windows 蓝牙端点通知失败或超出容量。"));
        }
        void Enqueue(BluetoothDeviceChange change) { if (!queue.Writer.TryWrite(change)) Fail(); }
        BluetoothEndpoint Read(DeviceInformation device) => new(device.Id, device.Name,
            device.Properties.TryGetValue(ConnectedProperty, out var value) && value is bool connected ? connected : null);
        void Added(DeviceWatcher sender, DeviceInformation device)
        {
            lock (gate)
            {
                if (disposed || failed) return;
                try
                {
                    devices[device.Id] = device;
                    if (devices.Count > 256) { Fail(); return; }
                    Enqueue(new(Read(device)));
                }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { Fail(); }
            }
        }
        void Updated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            lock (gate)
            {
                if (disposed || failed) return;
                try
                {
                    if (!devices.TryGetValue(update.Id, out var device)) { Fail(); return; }
                    // Updated contains only changed properties. Merge before inspecting Name/IsConnected.
                    device.Update(update); Enqueue(new(Read(device)));
                }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { Fail(); }
            }
        }
        void Removed(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            lock (gate)
            {
                if (disposed || failed) return;
                devices.Remove(update.Id); Enqueue(new(RemovedId: update.Id));
            }
        }
        void Completed(DeviceWatcher sender, object args) { lock (gate) if (!disposed && !failed) Enqueue(new(EnumerationCompleted: true)); }
        void Stopped(DeviceWatcher sender, object args) { lock (gate) if (!disposed) Fail(); }
        watcher.Added += Added; watcher.Updated += Updated; watcher.Removed += Removed;
        watcher.EnumerationCompleted += Completed; watcher.Stopped += Stopped;
        try
        {
            watcher.Start();
            await BluetoothDeviceObservation.RunAsync(names, queue.Reader, publish, token, () => Volatile.Read(ref failed));
        }
        finally
        {
            lock (gate) disposed = true;
            watcher.Added -= Added; watcher.Updated -= Updated; watcher.Removed -= Removed;
            watcher.EnumerationCompleted -= Completed; watcher.Stopped -= Stopped;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
    }
}
