using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record BluetoothDeviceValue(string Name, bool? Connected);
public sealed record BluetoothEndpoint(string Id, string Name, bool? Connected);
public sealed record BluetoothDeviceChange(BluetoothEndpoint? Endpoint = null, string? RemovedId = null, bool EnumerationCompleted = false);

// The source is restricted to paired Bluetooth AEPs. HID visibility and virtual USB devices never enter this state.
public static class BluetoothDeviceObservation
{
    public static async Task RunAsync(string[] selected, ChannelReader<BluetoothDeviceChange> changes,
        Action<BluetoothDeviceValue[], bool> publish, CancellationToken token, Func<bool>? failed = null)
    {
        var devices = new Dictionary<string, BluetoothEndpoint>(StringComparer.Ordinal);
        var ready = false; BluetoothDeviceValue[]? previous = null;
        using var initial = CancellationTokenSource.CreateLinkedTokenSource(token);
        initial.CancelAfter(TimeSpan.FromSeconds(15));
        while (await changes.WaitToReadAsync(ready ? token : initial.Token))
        {
            token.ThrowIfCancellationRequested();
            for (var count = 0; count < 256 && changes.TryRead(out var change); count++)
            {
                if (change.Endpoint is { } endpoint)
                {
                    if (string.IsNullOrWhiteSpace(endpoint.Id) || endpoint.Id.Length > 2048 || endpoint.Name.Length > 255 || endpoint.Name.Any(char.IsControl))
                        throw new IOException("蓝牙端点信息无效。");
                    devices[endpoint.Id] = endpoint;
                    if (devices.Count > 256) throw new IOException("蓝牙端点超出容量。");
                }
                if (change.RemovedId is { } id) devices.Remove(id);
                ready |= change.EnumerationCompleted;
            }
            if (failed?.Invoke() == true) throw new IOException("蓝牙连接状态通知丢失。");
            if (!ready) continue;
            initial.CancelAfter(Timeout.InfiniteTimeSpan);
            var samples = selected.Select(name =>
            {
                var matches = devices.Values.Where(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                // A missing name could belong to a selected endpoint; never infer disconnection from incomplete metadata.
                bool? connected = matches.Any(d => d.Connected == true) ? true :
                    matches.Any(d => d.Connected is null) || devices.Values.Any(d => d.Name.Length == 0 && d.Connected != false) ? null : false;
                return new BluetoothDeviceValue(name, connected);
            }).ToArray();
            var baseline = previous is null || samples.Any(s => s.Connected.HasValue && previous.Any(p => p.Name == s.Name && !p.Connected.HasValue));
            if (baseline || !samples.SequenceEqual(previous!)) publish(samples, baseline);
            previous = samples;
        }
        if (!token.IsCancellationRequested) throw new IOException("蓝牙连接状态通知已停止。");
    }
}
