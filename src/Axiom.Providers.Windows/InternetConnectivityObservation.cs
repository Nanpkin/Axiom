using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public static class InternetConnectivityObservation
{
    // Called only by the source's dedicated MTA thread. Waiting never moves COM access onto a thread-pool continuation.
    public static void Run(ChannelReader<byte> notifications, Func<uint> query, Action<InternetConnectivitySample> publish,
        CancellationToken token, Func<bool>? notificationFailed = null)
    {
        var state = new InternetConnectivityState();
        Refresh();
        while (notifications.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
        {
            if (!notifications.TryRead(out _)) continue;
            for (var i = 1; i < 64 && notifications.TryRead(out _); i++) { }
            Refresh();
        }
        token.ThrowIfCancellationRequested();
        throw new IOException("Internet状态通知已结束。");

        void Refresh()
        {
            token.ThrowIfCancellationRequested();
            if (notificationFailed?.Invoke() == true) throw new IOException("Internet状态通知丢失。");
            uint? flags;
            try { flags = query(); }
            catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException)) { flags = null; }
            token.ThrowIfCancellationRequested();
            if (notificationFailed?.Invoke() == true) throw new IOException("Internet查询期间通知丢失。");
            if (state.Read(flags) is { } sample) publish(sample);
        }
    }
}
