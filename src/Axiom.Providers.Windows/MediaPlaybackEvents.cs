using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public enum MediaPlaybackStatus { Unknown, NoSession, Playing, Paused, Stopped }
public sealed record MediaPlaybackSample(MediaPlaybackStatus Status, bool Baseline, string? Reason = null)
{
    public bool? Matches(string resource) => Status == MediaPlaybackStatus.Unknown ? null : resource switch
    {
        "playing" => Status == MediaPlaybackStatus.Playing, "paused" => Status == MediaPlaybackStatus.Paused,
        "stopped" => Status == MediaPlaybackStatus.Stopped, _ => throw new ArgumentException("媒体状态资源无效。")
    };
}
public interface IMediaPlaybackSource : IDisposable
{
    ChannelReader<MediaPlaybackSample> Events { get; }
    bool Failed { get; }
}
internal interface IMediaPlaybackSession : IDisposable
{
    event Action PlaybackChanged;
    MediaPlaybackStatus Read();
    bool Same(IMediaPlaybackSession other);
}
internal interface IMediaPlaybackApi : IDisposable
{
    event Action CurrentChanged;
    IMediaPlaybackSession? Current();
}

// One serialized worker owns query/subscription calls. Callbacks only invalidate generations and queue bounded hints.
internal sealed class MediaPlaybackHub
{
    private readonly object gate = new();
    private readonly HashSet<Lease> clients = [];
    private readonly Channel<byte> hints = Channel.CreateBounded<byte>(64);
    private readonly CancellationTokenSource stop = new();
    private readonly Func<IMediaPlaybackApi> open;
    private MediaPlaybackSample latest = new(MediaPlaybackStatus.Unknown, true, "正在连接Windows当前媒体通知。");
    private long generation;
    private bool baseline = true, closing, failed;
    internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal MediaPlaybackHub(Func<IMediaPlaybackApi> open)
    {
        this.open = open;
        var worker = new Thread(Run) { IsBackground = true, Name = "Axiom current media playback state" };
        if (OperatingSystem.IsWindows()) worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }
    internal IMediaPlaybackSource Subscribe()
    {
        lock (gate)
        {
            if (closing) throw new IOException("媒体通知仍在结束或已失效，请暂停后重新启用。");
            var lease = new Lease(this); clients.Add(lease); lease.Queue.Writer.TryWrite(latest with { Baseline = true }); return lease;
        }
    }
    private void SelectionChanged()
    {
        lock (gate)
        {
            if (closing) return;
            generation++; baseline = true;
            PublishLocked(new(MediaPlaybackStatus.Unknown, true, "Windows当前媒体会话已切换，正在建立新基线。"));
            HintLocked();
        }
    }
    private void Hint(long expected)
    { lock (gate) if (!closing && expected == generation) HintLocked(); }
    private void HintLocked()
    {
        if (hints.Writer.TryWrite(1)) return;
        FailLocked(); // Notify all leases immediately, even while a playback query is blocked.
    }
    private void FailLocked()
    {
        failed = true; closing = true;
        foreach (var client in clients) { client.FailedValue = true; client.Queue.Writer.TryComplete(new IOException("媒体通知丢失或不可用。")); }
        hints.Writer.TryComplete(new IOException("媒体通知丢失或不可用。"));
    }
    private void PublishLocked(MediaPlaybackSample sample)
    {
        latest = sample;
        foreach (var client in clients)
            if (!client.Queue.Writer.TryWrite(sample))
            { client.FailedValue = true; client.Queue.Writer.TryComplete(new IOException("媒体状态样本积压，检测未知。")); }
    }
    private void Remove(Lease lease)
    {
        var last = false;
        lock (gate)
        {
            if (!clients.Remove(lease)) return;
            lease.Queue.Writer.TryComplete();
            if (clients.Count == 0) { closing = true; last = true; }
        }
        if (last) { stop.Cancel(); hints.Writer.TryComplete(); Stopped.Task.Wait(TimeSpan.FromSeconds(5)); }
    }
    private void Run()
    {
        IMediaPlaybackApi? api = null; IMediaPlaybackSession? selected = null; long selectedGeneration = -1;
        try
        {
            stop.Token.ThrowIfCancellationRequested(); api = open();
            stop.Token.ThrowIfCancellationRequested(); api.CurrentChanged += SelectionChanged; // Subscribe before either selection or playback reads.
            Refresh();
            while (hints.Reader.WaitToReadAsync(stop.Token).AsTask().GetAwaiter().GetResult())
            {
                for (var i = 0; i < 64 && hints.Reader.TryRead(out _); i++) { }
                Refresh();
            }
            stop.Token.ThrowIfCancellationRequested(); throw new IOException("媒体通知已结束。");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { lock (gate) FailLocked(); }
        finally
        {
            try
            {
                Cleanup(() => selected?.Dispose());
                if (api is not null) Cleanup(() => api.CurrentChanged -= SelectionChanged);
                Cleanup(() => api?.Dispose());
            }
            finally
            {
                lock (gate) { closing = true; foreach (var client in clients) client.Queue.Writer.TryComplete(); Stopped.TrySetResult(); }
            }
        }
        void Cleanup(Action release)
        {
            try { release(); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { lock (gate) FailLocked(); }
        }
        void Refresh()
        {
            stop.Token.ThrowIfCancellationRequested(); long version;
            lock (gate) { if (failed) throw new IOException("媒体通知失效。"); version = generation; }
            using var current = api!.Current();
            lock (gate) if (version != generation || closing) return;
            if (selectedGeneration != version)
            {
                selected?.Dispose(); selected = api.Current(); selectedGeneration = version;
                if (selected is not null) selected.PlaybackChanged += () => Hint(version);
            }
            else if ((selected is null) != (current is null) || selected is not null && !selected.Same(current!))
            { SelectionChanged(); return; }
            MediaPlaybackStatus status;
            try { status = selected?.Read() ?? MediaPlaybackStatus.NoSession; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { status = MediaPlaybackStatus.Unknown; }
            stop.Token.ThrowIfCancellationRequested();
            using var verified = api.Current();
            if ((selected is null) != (verified is null) || selected is not null && !selected.Same(verified!)) { SelectionChanged(); return; }
            lock (gate)
            {
                if (failed) throw new IOException("媒体查询期间通知失效。");
                if (closing || version != generation) return;
                var snapshot = baseline || latest.Status == MediaPlaybackStatus.Unknown && status != MediaPlaybackStatus.Unknown;
                if (!baseline && latest.Status == status) return;
                baseline = false;
                PublishLocked(new(status, snapshot, status switch
                { MediaPlaybackStatus.Unknown => "当前媒体播放状态未确认，不会当作暂停或停止。", MediaPlaybackStatus.NoSession => "Windows没有选中的当前媒体会话，三个播放条件均不成立。", _ => null }));
            }
        }
    }
    private sealed class Lease(MediaPlaybackHub hub) : IMediaPlaybackSource
    {
        internal readonly Channel<MediaPlaybackSample> Queue = Channel.CreateBounded<MediaPlaybackSample>(64);
        internal volatile bool FailedValue;
        public ChannelReader<MediaPlaybackSample> Events => Queue.Reader;
        public bool Failed => FailedValue;
        public void Dispose() => hub.Remove(this);
    }
}
