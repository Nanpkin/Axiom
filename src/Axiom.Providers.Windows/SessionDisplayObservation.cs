using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public static class SessionDisplayObservation
{
    public static async Task RunAsync(ChannelReader<string> events, Action<SessionDisplaySample> publish, CancellationToken token, Func<bool>? failed = null)
    {
        var state = new SessionDisplayState();
        await foreach (var kind in events.ReadAllAsync(token))
        {
            token.ThrowIfCancellationRequested();
            if (failed?.Invoke() == true) throw new IOException("屏幕电源通知失效，丢弃积压状态。");
            if (kind is not ("screen-on" or "screen-dim" or "screen-off" or "screen-reset")) continue;
            var sample = state.Read(SessionDisplayState.Decode(kind));
            if (sample is not null) publish(sample);
        }
        if (!token.IsCancellationRequested) throw new IOException("屏幕电源通知已停止。");
    }
}
