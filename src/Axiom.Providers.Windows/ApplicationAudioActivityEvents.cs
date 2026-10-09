using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record ApplicationAudioActivitySample(string Path, bool? Active, bool Baseline);
public interface IApplicationAudioActivitySource : IDisposable
{
    ChannelReader<ApplicationAudioActivitySample> Events { get; }
    bool Failed { get; }
}
internal sealed record ApplicationAudioReading(string? Path, bool? Active);
internal interface IApplicationAudioActivitySession
{
    bool ProcessExited { get; }
    int State { get; }
    AudioSessionIdentity Identify(out string? path);
    void WatchExit();
}
internal static class ApplicationAudioSessionReading
{
    internal static ApplicationAudioReading Read(IApplicationAudioActivitySession session, string[] paths, string? pinnedPath)
    {
        var path = pinnedPath;
        try
        {
            if (session.ProcessExited) return new(path, false);
            var state = session.State;
            if (state is 0 or 2) return new(path, false);
            if (state != 1) return new(path, null);
            var owned = session.Identify(out var candidate);
            if (owned == AudioSessionIdentity.Other) return new(null, false);
            if (owned != AudioSessionIdentity.Owned || candidate is null) return new(path, null);
            path = candidate;
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) return new(path, false);
            session.WatchExit(); state = session.State;
            if (session.ProcessExited) return new(path, false);
            return new(path, state is 0 or 2 ? false : state == 1 ? true : null);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return new(path, null); }
    }
}
internal sealed record ApplicationAudioHint(string Kind, long Generation = -1, IDisposable? Created = null) : IDisposable
{ public void Dispose() => Created?.Dispose(); }
internal interface IApplicationAudioActivityApi : IDisposable
{
    ApplicationAudioReading[] Read(ApplicationAudioHint hint, long selection, string[] paths);
}

internal static class ApplicationAudioProjection
{
    internal static bool? Match(IEnumerable<ApplicationAudioReading> readings, string path)
    {
        var relevant = readings.Where(r => r.Path is null || string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (relevant.Any(r => r.Path is not null && r.Active == true)) return true;
        return relevant.Any(r => r.Active is null) ? null : false;
    }
}

// A single MTA owns the actual session subscriptions/queries. Callbacks only queue bounded hints.
internal sealed class ApplicationAudioActivityHub
{
    private readonly object gate = new();
    private readonly HashSet<Lease> clients = [];
    private readonly Dictionary<string, ApplicationAudioActivitySample> latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<ApplicationAudioHint> hints = Channel.CreateBounded<ApplicationAudioHint>(64);
    private readonly Func<Action<ApplicationAudioHint>, IApplicationAudioActivityApi> open;
    private long selection, revision;
    private bool closing, failed;
    internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ApplicationAudioActivityHub(Func<Action<ApplicationAudioHint>, IApplicationAudioActivityApi> open)
    {
        this.open = open;
        var thread = new Thread(Run) { IsBackground = true, Name = "Axiom application audio activity" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }
    internal IApplicationAudioActivitySource Subscribe(string[] paths)
    {
        lock (gate)
        {
            if (closing) throw new IOException("应用播放通知仍在结束或已失效。");
            if (clients.Count >= 32 || clients.SelectMany(c => c.Paths).Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 128)
                throw new IOException("应用播放检测资源容量已满。");
            var client = new Lease(this, paths); clients.Add(client);
            foreach (var path in paths) client.Queue.Writer.TryWrite(latest.TryGetValue(path, out var sample) ? sample with { Baseline = true } : new(path, null, true));
            EnqueueLocked(new("sync")); return client;
        }
    }
    internal void Notify(ApplicationAudioHint hint)
    {
        var release = false;
        lock (gate)
        {
            if (closing || hint.Generation >= 0 && hint.Generation != selection) release = true;
            else if (hint.Kind == "failed") { FailLocked(); release = true; }
            else
            {
                if (hint.Kind == "default") selection++;
                if (hint.Kind is "default" or "exit")
                    foreach (var path in clients.SelectMany(c => c.Paths).Distinct(StringComparer.OrdinalIgnoreCase)) PublishLocked(new(path, null, false));
                revision++; release = !EnqueueLocked(hint);
            }
        }
        if (release) hint.Dispose(); // Never hold the observer gate while releasing a native callback reference.
    }
    private bool EnqueueLocked(ApplicationAudioHint hint)
    {
        if (hints.Writer.TryWrite(hint)) return true;
        FailLocked(); return false;
    }
    private void FailLocked()
    {
        closing = failed = true;
        foreach (var client in clients) { client.FailedValue = true; client.Queue.Writer.TryComplete(new IOException("应用音频通知丢失，检测未知。")); }
        hints.Writer.TryComplete(new IOException("应用音频通知丢失。"));
    }
    private void PublishLocked(ApplicationAudioActivitySample sample)
    {
        latest[sample.Path] = sample;
        foreach (var client in clients.Where(c => c.Paths.Contains(sample.Path, StringComparer.OrdinalIgnoreCase)))
            if (!client.Queue.Writer.TryWrite(sample)) { client.FailedValue = true; client.Queue.Writer.TryComplete(new IOException("应用音频状态积压，检测未知。")); }
    }
    private void Remove(Lease client)
    {
        var last = false;
        lock (gate)
        {
            if (!clients.Remove(client)) return;
            client.Queue.Writer.TryComplete();
            if (clients.Count == 0) { closing = true; hints.Writer.TryComplete(); last = true; }
            else EnqueueLocked(new("sync"));
        }
        if (last) Stopped.Task.Wait(TimeSpan.FromSeconds(5));
    }
    private T Native<T>(Func<T> query)
    {
        var done = false;
        using var deadline = new Timer(_ => { lock (gate) { if (!done) FailLocked(); } }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        try { return query(); }
        finally { lock (gate) done = true; }
    }
    private void Run()
    {
        IApplicationAudioActivityApi? api = null;
        try
        {
            api = Native(() => open(Notify));
            while (hints.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                using var hint = hints.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
                long version, before; string[] paths;
                lock (gate)
                {
                    if (closing) break;
                    if (hint.Generation >= 0 && hint.Generation != selection) continue;
                    version = selection; before = revision; paths = [.. clients.SelectMany(c => c.Paths).Distinct(StringComparer.OrdinalIgnoreCase)];
                }
                var readings = Native(() => api.Read(hint, version, paths));
                lock (gate)
                {
                    if (failed) throw new IOException("应用音频通知已失效。");
                    if (closing || selection != version || revision != before) continue;
                    foreach (var path in paths)
                    {
                        var value = ApplicationAudioProjection.Match(readings, path);
                        var existed = latest.TryGetValue(path, out var prior);
                        if (existed && prior!.Active == value) continue;
                        PublishLocked(new(path, value, !existed || prior!.Active is null && value.HasValue));
                    }
                    foreach (var path in latest.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).ToArray()) latest.Remove(path);
                }
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { lock (gate) FailLocked(); }
        finally
        {
            try { api?.Dispose(); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { lock (gate) FailLocked(); }
            finally
            {
                while (hints.Reader.TryRead(out var queued)) queued.Dispose();
                lock (gate) { closing = true; foreach (var client in clients) client.Queue.Writer.TryComplete(); Stopped.TrySetResult(); }
            }
        }
    }
    private sealed class Lease(ApplicationAudioActivityHub owner, string[] paths) : IApplicationAudioActivitySource
    {
        internal readonly string[] Paths = paths;
        internal readonly Channel<ApplicationAudioActivitySample> Queue = Channel.CreateBounded<ApplicationAudioActivitySample>(256);
        internal volatile bool FailedValue;
        public ChannelReader<ApplicationAudioActivitySample> Events => Queue.Reader;
        public bool Failed => FailedValue;
        public void Dispose() => owner.Remove(this);
    }
}
