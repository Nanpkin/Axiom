using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public readonly record struct ForegroundCoverIdentity(nint Window, uint ProcessId, uint ThreadId, long Creation, long SelectionEpoch = 0);
public sealed record ForegroundCoverReading(ForegroundCoverIdentity? Selection, bool? Covers);
public sealed record ForegroundCoverSample(bool? Covers, bool Baseline);
public readonly record struct ForegroundCoverHint(bool Reset = false);
public interface IForegroundScreenCoverSource : IDisposable
{
    ChannelReader<ForegroundCoverHint> Events { get; }
    bool Failed { get; }
    ForegroundCoverReading Read();
}
internal static class ForegroundScreenCoverGeometry
{
    public static bool Covers(MonitorRectangle frame, MonitorRectangle screen)
    {
        if (frame.Right <= frame.Left || frame.Bottom <= frame.Top || screen.Right <= screen.Left || screen.Bottom <= screen.Top)
            throw new ArgumentException("前台边框或显示器范围无效。");
        return frame.Left <= screen.Left && frame.Top <= screen.Top && frame.Right >= screen.Right && frame.Bottom >= screen.Bottom;
    }
}
public static class ForegroundScreenCoverObservation
{
    public static async Task RunAsync(IForegroundScreenCoverSource source, Action<ForegroundCoverSample> publish, CancellationToken token)
    {
        ForegroundCoverReading? previous = null;
        Refresh();
        await foreach (var hint in source.Events.ReadAllAsync(token))
        {
            Check(); var reset = hint.Reset;
            for (var i = 1; i < 64 && source.Events.TryRead(out var next); i++) reset |= next.Reset;
            if (reset) Apply(new(null, null)); // preserve destruction/resume even if a new foreground report follows
            Refresh();
        }
        if (!token.IsCancellationRequested) throw new IOException("前台覆盖屏幕通知已结束。");
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (source.Failed) throw new IOException("前台覆盖屏幕通知丢失；丢弃缓存。");
        }
        void Refresh()
        {
            Check(); ForegroundCoverReading reading;
            try { reading = source.Read(); }
            catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException)) { reading = new(null, null); }
            Check(); Apply(reading);
        }
        void Apply(ForegroundCoverReading reading)
        {
            Check();
            var baseline = previous is null || reading.Covers.HasValue && (!previous.Covers.HasValue || previous.Selection != reading.Selection);
            if (previous is null || baseline || reading.Covers != previous.Covers) publish(new(reading.Covers, baseline));
            previous = reading;
        }
    }
}
