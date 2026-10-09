namespace Axiom.Providers.Windows;

public static class NetworkCategoryObservation
{
    // This stays on the source's one MTA thread; callbacks never enumerate or call COM.
    public static void Run(NetworkCategoryNotifications notifications, Func<NetworkCategorySnapshot> query,
        Action<NetworkCategorySample> publish, CancellationToken token)
    {
        var state = new NetworkCategoryState(); Refresh();
        while (notifications.Events.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
        {
            for (var i = 0; i < 64 && notifications.Events.TryRead(out _); i++) { }
            Refresh();
        }
        token.ThrowIfCancellationRequested(); throw new IOException("网络分类通知已结束。");
        void Refresh()
        {
            token.ThrowIfCancellationRequested();
            if (notifications.Failed) throw new IOException("网络分类通知丢失。");
            var version = notifications.Version; NetworkCategorySnapshot snapshot;
            try { snapshot = query(); }
            catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException)) { snapshot = new([], false); }
            token.ThrowIfCancellationRequested();
            if (notifications.Failed) throw new IOException("网络分类查询期间通知丢失。");
            if (version != notifications.Version) return; // A queued hint causes a fresh read; do not publish the superseded query.
            if (state.Read(snapshot) is { } sample) publish(sample);
        }
    }
}
