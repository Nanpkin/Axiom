using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public static class SessionPresenceObservation
{
    public static async Task RunAsync(ChannelReader<string> events, Action<SessionPresenceSample> publish, CancellationToken token, Func<bool>? failed = null)
    {
        var state = new SessionPresenceState();
        await foreach (var kind in events.ReadAllAsync(token))
        {
            Check();
            if (!kind.StartsWith("presence-", StringComparison.Ordinal)) continue;
            var latest = SessionPresenceState.Decode(kind);
            var reset = !latest.HasValue;
            // Bounded coalescing keeps the Unknown boundary even when followed by Known.
            for (var i = 1; i < 64 && events.TryRead(out var buffered); i++)
            {
                if (!buffered.StartsWith("presence-", StringComparison.Ordinal)) continue;
                latest = SessionPresenceState.Decode(buffered); reset |= !latest.HasValue;
            }
            if (reset) Publish(null);
            if (latest.HasValue) Publish(latest);
        }
        if (!token.IsCancellationRequested) throw new IOException("用户在场通知已结束。");
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (failed?.Invoke() == true) throw new IOException("用户在场通知丢失；丢弃缓存。");
        }
        void Publish(uint? value)
        {
            Check();
            if (state.Read(value) is { } sample) publish(sample);
        }
    }
}
