using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public interface ISessionLockStateSource : IDisposable
{
    ChannelReader<string> Events { get; }
    bool Failed { get; }
    bool? Read(CancellationToken token);
}
public sealed record SessionLockStateSample(bool? Locked, bool Baseline);
public static class SessionLockStateObservation
{
    public static async Task RunAsync(ISessionLockStateSource source, Action<SessionLockStateSample> publish, CancellationToken token)
    {
        bool? previous = null; var first = true;
        Refresh(true);
        await foreach (var kind in source.Events.ReadAllAsync(token))
        {
            Check(); if (kind is not ("session-lock-changed" or "session-lock-reset" or "session-lock-resumed")) continue;
            var reset = kind != "session-lock-changed"; var read = kind != "session-lock-reset";
            for (var i = 1; i < 64 && source.Events.TryRead(out var hint); i++)
            {
                if (hint is not ("session-lock-changed" or "session-lock-reset" or "session-lock-resumed")) continue;
                reset |= hint != "session-lock-changed"; read = hint != "session-lock-reset";
            }
            if (reset) Apply(null, false);
            if (read) Refresh(reset);
        }
        if (!token.IsCancellationRequested) throw new IOException("会话锁定状态通知已结束。");
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (source.Failed) throw new IOException("会话锁定状态通知丢失；丢弃缓存。");
        }
        void Refresh(bool baseline)
        {
            Check(); bool? value;
            try { value = source.Read(token); }
            catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException)) { value = null; }
            Check(); Apply(value, baseline);
        }
        void Apply(bool? value, bool baseline)
        {
            Check(); baseline |= first || !previous.HasValue && value.HasValue;
            if (first || baseline || previous != value) publish(new(value, baseline));
            first = false; previous = value;
        }
    }
}
