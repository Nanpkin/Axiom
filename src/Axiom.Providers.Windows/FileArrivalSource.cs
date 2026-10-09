using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record FileArrivalWatch(string Resource, string Directory, string Kind);
public static class FileArrivalSource
{
    public static bool Matches(string? name, string kind)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return kind switch
        {
            "pdf" => extension == ".pdf",
            "office" => extension is ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx",
            "image" => extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff",
            "archive" => extension is ".zip" or ".7z" or ".rar" or ".tar" or ".gz",
            "any" => true,
            _ => throw new ArgumentException("文件类别无效。")
        };
    }
    public static async Task RunAsync(FileArrivalWatch[] targets, Action ready, Action<string[]> publish, CancellationToken token, TimeProvider? timeProvider = null)
    {
        if (targets.Length is < 1 or > 16 || targets.Select(t => t.Resource).Distinct().Count() != targets.Length) throw new ArgumentException("文件检测最多16种不同的目录/类别组合。");
        foreach (var target in targets) { ArgumentException.ThrowIfNullOrWhiteSpace(target.Directory); _ = Matches("synthetic.pdf", target.Kind); }
        var time = timeProvider ?? TimeProvider.System;
        var queue = Channel.CreateBounded<string[]>(256); var watchers = new List<FileSystemWatcher>();
        IOException? failure = null; Task<string[]>? next = null;
        void Fail(string reason) { Interlocked.CompareExchange(ref failure, new IOException(reason), null); queue.Writer.TryComplete(failure); }
        void Check() { token.ThrowIfCancellationRequested(); if (Volatile.Read(ref failure) is { } error) throw error; }
        try
        {
            foreach (var group in targets.GroupBy(t => t.Directory, StringComparer.OrdinalIgnoreCase))
            {
                Check();
                if (new DriveInfo(Path.GetPathRoot(group.Key)!).DriveType == DriveType.Network) throw new IOException("文件到达检测不支持网络驱动器。");
                var subscriptions = group.ToArray();
                var watcher = new FileSystemWatcher(group.Key) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false, InternalBufferSize = 8192 };
                watchers.Add(watcher);
                void Arrived(object sender, FileSystemEventArgs change)
                {
                    if (Volatile.Read(ref failure) is not null || token.IsCancellationRequested) return;
                    var matches = subscriptions.Where(t => Matches(change.Name, t.Kind)).Select(t => t.Resource).ToArray();
                    if (matches.Length > 0 && !queue.Writer.TryWrite(matches)) Fail("文件通知过载，检测状态未知；不会扫描目录补造历史。");
                }
                watcher.Created += Arrived; watcher.Renamed += (sender, change) => Arrived(sender, change);
                watcher.Error += (_, _) => Fail("文件夹通知中断，检测状态未知；请检查目录和权限后暂停并重新启用。");
                watcher.EnableRaisingEvents = true;
            }
            Check(); ready(); next = queue.Reader.ReadAsync(token).AsTask();
            while (true)
            {
                var pending = new HashSet<string>(await next); Check(); next = queue.Reader.ReadAsync(token).AsTask();
                using var batch = CancellationTokenSource.CreateLinkedTokenSource(token);
                var due = Task.Delay(TimeSpan.FromSeconds(1), time, batch.Token);
                try
                {
                    while (!due.IsCompleted)
                    {
                        Check(); await Task.WhenAny(due, next); Check();
                        if (due.IsCompleted) break;
                        pending.UnionWith(await next); next = queue.Reader.ReadAsync(token).AsTask();
                    }
                    await due; Check(); publish([.. pending]);
                }
                finally { batch.Cancel(); try { await due; } catch (OperationCanceledException) when (batch.IsCancellationRequested) { } }
            }
        }
        finally
        {
            foreach (var watcher in watchers) watcher.Dispose();
            queue.Writer.TryComplete();
            if (next is not null) try { await next; } catch (Exception error) when (error is IOException or OperationCanceledException or ChannelClosedException) { }
        }
    }
}
