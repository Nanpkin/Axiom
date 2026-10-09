using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public static class PowerStateObservation
{
    public static async Task RunAsync(string[] resources, ChannelReader<string> notifications, Func<PowerStateSnapshot> query,
        Action<PowerStateValue[], bool> publish, CancellationToken token, Func<bool>? notificationFailed = null)
    {
        PowerStateValue[]? previous = null;
        Refresh(true);
        await foreach (var kind in notifications.ReadAllAsync(token))
        {
            if (kind is not ("power-state-changed" or "resumed")) continue;
            var baseline = kind == "resumed";
            for (var i = 1; i < 64 && notifications.TryRead(out var buffered); i++) baseline |= buffered == "resumed";
            Refresh(baseline);
        }
        if (!token.IsCancellationRequested) throw new IOException("供电状态通知已结束。");

        void Refresh(bool baseline)
        {
            token.ThrowIfCancellationRequested();
            if (notificationFailed?.Invoke() == true) throw new IOException("供电状态通知丢失。");
            PowerStateSnapshot? status;
            try { status = query(); }
            catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException)) { status = null; }
            if (notificationFailed?.Invoke() == true) throw new IOException("供电状态查询期间通知丢失。");
            var samples = PowerStateState.Samples(resources, status);
            baseline |= previous is null || samples.Any(s => s.Satisfied.HasValue && previous.Any(p => p.ResourceId == s.ResourceId && !p.Satisfied.HasValue));
            if (baseline || !samples.SequenceEqual(previous!)) publish(samples, baseline);
            previous = samples;
        }
    }
}
