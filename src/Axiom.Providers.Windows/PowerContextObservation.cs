using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record PowerContextValue(string ResourceId, bool? Satisfied);

internal static class PowerContextObservation
{
    public static async Task RunAsync(string[] resources, ChannelReader<string> notifications, string changedKind, string resetKind, Func<PowerContextValue[]> query,
        Action<PowerContextValue[], bool> publish, CancellationToken token, Func<bool>? notificationFailed = null)
    {
        PowerContextValue[]? previous = null;
        Refresh(true);
        await foreach (var kind in notifications.ReadAllAsync(token))
        {
            if (kind != changedKind && kind != resetKind && kind != "resumed") continue;
            var baseline = kind == "resumed";
            var reset = kind == resetKind;
            var queryCurrent = !reset;
            for (var i = 1; i < 64 && notifications.TryRead(out var buffered); i++)
            {
                baseline |= buffered == "resumed";
                reset |= buffered == resetKind;
                if (buffered == changedKind || buffered == resetKind || buffered == "resumed") queryCurrent = buffered != resetKind;
            }
            if (reset) Refresh(false, true); // release Desired before considering a post-reset current report
            if (queryCurrent) Refresh(baseline || reset);
        }
        if (!token.IsCancellationRequested) throw new IOException("电源上下文通知已结束。");

        PowerContextValue[] Unknown() => [.. resources.Select(id => new PowerContextValue(id, null))];

        void Refresh(bool baseline, bool reset = false)
        {
            token.ThrowIfCancellationRequested();
            if (notificationFailed?.Invoke() == true) throw new IOException("电源上下文通知丢失。");
            PowerContextValue[] samples;
            try { samples = reset ? Unknown() : query(); }
            catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException)) { samples = Unknown(); }
            if (notificationFailed?.Invoke() == true) throw new IOException("电源上下文查询期间通知丢失。");
            baseline |= previous is null || samples.Any(s => s.Satisfied.HasValue && previous.Any(p => p.ResourceId == s.ResourceId && !p.Satisfied.HasValue));
            if (baseline || !samples.SequenceEqual(previous!)) publish(samples, baseline);
            previous = samples;
        }
    }
}
