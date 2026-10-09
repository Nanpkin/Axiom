using System.Threading.Channels;

namespace Axiom.Providers.Windows;

// Native callbacks only enqueue hints. This consumer bounds each batch to the source's 64-slot capacity.
public static class UsbDeviceObservation
{
    public static async Task RunAsync(string[] selected, ChannelReader<string> notifications, Func<string[]> query,
        Action<UsbDeviceValue[], bool> publish, CancellationToken token, Func<bool>? notificationFailed = null)
    {
        var state = new UsbDeviceState(selected); UsbDeviceValue[]? previous = null;
        Refresh(true);
        await foreach (var kind in notifications.ReadAllAsync(token))
        {
            if (kind is not ("usb-arrived" or "usb-removed" or "resumed")) continue;
            var baseline = kind == "resumed";
            for (var i = 1; i < 64 && notifications.TryRead(out var buffered); i++) baseline |= buffered == "resumed";
            Refresh(baseline);
        }
        if (!token.IsCancellationRequested) throw new IOException("USB 通知已结束，检测状态未知。");

        void Refresh(bool baseline)
        {
            token.ThrowIfCancellationRequested();
            if (notificationFailed?.Invoke() == true) throw new IOException("USB 通知丢失，检测状态未知。");
            try { state.Replace(query()); }
            catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException)) { state.Replace(null); }
            if (notificationFailed?.Invoke() == true) throw new IOException("USB 查询期间通知丢失，检测状态未知。");
            var samples = state.Samples();
            baseline |= previous is null || samples.Any(s => s.Present.HasValue && previous.Any(p => p.InstanceId == s.InstanceId && !p.Present.HasValue));
            if (baseline || !samples.SequenceEqual(previous!)) publish(samples, baseline);
            previous = samples;
        }
    }
}
