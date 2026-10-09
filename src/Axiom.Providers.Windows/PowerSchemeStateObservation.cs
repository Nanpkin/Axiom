using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public static class PowerSchemeStateObservation
{
    public static async Task RunAsync(string[] resources, ChannelReader<string> notifications, Func<Guid> query,
        Action<PowerSchemeStateValue[], bool> publish, CancellationToken token, Func<bool>? notificationFailed = null)
    {
        PowerSchemeStateValue[]? previous = null;
        Refresh(true);
        await foreach (var kind in notifications.ReadAllAsync(token))
        {
            if (kind is not ("power-scheme-changed" or "power-scheme-reset" or "resumed")) continue;
            var baseline = kind == "resumed";
            var reset = kind == "power-scheme-reset";
            var queryCurrent = !reset;
            for (var i = 1; i < 64 && notifications.TryRead(out var buffered); i++)
            {
                baseline |= buffered == "resumed";
                reset |= buffered == "power-scheme-reset";
                if (buffered is "power-scheme-changed" or "power-scheme-reset" or "resumed") queryCurrent = buffered != "power-scheme-reset";
            }
            if (reset) Refresh(false, true); // release Desired before considering a post-reset current report
            if (queryCurrent) Refresh(baseline || reset);
        }
        if (!token.IsCancellationRequested) throw new IOException("电源方案状态通知已结束。");

        void Refresh(bool baseline, bool reset = false)
        {
            token.ThrowIfCancellationRequested();
            if (notificationFailed?.Invoke() == true) throw new IOException("电源方案状态通知丢失。");
            Guid? status;
            try { status = reset ? null : query(); }
            catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException)) { status = null; }
            if (notificationFailed?.Invoke() == true) throw new IOException("电源方案状态查询期间通知丢失。");
            var samples = PowerSchemeStateState.Samples(resources, status);
            baseline |= previous is null || samples.Any(s => s.Satisfied.HasValue && previous.Any(p => p.ResourceId == s.ResourceId && !p.Satisfied.HasValue));
            if (baseline || !samples.SequenceEqual(previous!)) publish(samples, baseline);
            previous = samples;
        }
    }
}
