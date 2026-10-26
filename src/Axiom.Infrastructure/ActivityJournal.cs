using System.Threading.Channels;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ActivityJournal : IActivityHistory, IAsyncDisposable
{
    public const int MaximumDetailLength = 40000; // Worst-case JSON escaping still fits one existing IPC frame.
    private abstract record Work;
    private sealed record DetailWork(long Id, CancellationToken Token, TaskCompletionSource<ActivityRecord?> Done) : Work;
    private sealed record AppendWork(ActivityEvent Entry) : Work;
    private sealed record ReadWork(long? BeforeId, int PageSize, CancellationToken Token, TaskCompletionSource<ActivityPage> Done) : Work;
    private sealed record ClearWork(DateTimeOffset BeforeUtc, CancellationToken Token, TaskCompletionSource<ActivityCleanupResult> Done) : Work;
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(512)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly SqliteActivityStore? store;
    private readonly Task worker;
    private long missed;
    private string? writeError;

    public ActivityJournal(SqliteActivityStore? store, string? error = null) { this.store = store; writeError = error; worker = RunAsync(); }

    public void Record(ActivityEvent entry)
    {
        static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
        var detail = entry.Detail.Trim();
        if (detail.Length > MaximumDetailLength) detail = detail[..(MaximumDetailLength - 32)] + "\n（详情超过记录上限，后续未保存）";
        entry = entry with { Category = Limit(entry.Category, 40), Title = Limit(entry.Title, 120),
            Detail = detail, Subject = entry.Subject is null ? null : Limit(entry.Subject, 120) };
        if (!queue.Writer.TryWrite(new AppendWork(entry))) Interlocked.Increment(ref missed);
    }

    public async Task<ActivityPage> ReadAsync(long? beforeId, int pageSize, CancellationToken token)
    {
        var done = new TaskCompletionSource<ActivityPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new ReadWork(beforeId, pageSize, token, done), token);
        return await done.Task.WaitAsync(token);
    }

    public Task<ActivityPage> ReadAsync(long? beforeId, CancellationToken token) =>
        ReadAsync(beforeId, SqliteActivityStore.DefaultPageSize, token);

    public async Task<ActivityRecord?> ReadDetailAsync(long id, CancellationToken token)
    {
        var done = new TaskCompletionSource<ActivityRecord?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new DetailWork(id, token, done), token);
        return await done.Task.WaitAsync(token);
    }

    public async Task<ActivityCleanupResult> ClearBeforeAsync(DateTimeOffset beforeUtc, CancellationToken token)
    {
        var done = new TaskCompletionSource<ActivityCleanupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new ClearWork(beforeUtc, token, done), token);
        return await done.Task.WaitAsync(token);
    }

    private async Task RunAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync())
        {
            try
            {
                switch (work)
                {
                    case AppendWork append:
                        var batch = new List<ActivityEvent> { append.Entry };
                        while (batch.Count < 64 && queue.Reader.TryPeek(out var next) && next is AppendWork && queue.Reader.TryRead(out next))
                            batch.Add(((AppendWork)next).Entry);
                        try
                        {
                            if (store is null) { Interlocked.Add(ref missed, batch.Count); break; }
                            store.Append(batch);
                        }
                        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
                        { writeError = error.Message; Interlocked.Add(ref missed, batch.Count); }
                        break;
                    case DetailWork detail:
                        detail.Token.ThrowIfCancellationRequested();
                        if (store is null) throw new InvalidOperationException("活动记录存储不可用。");
                        detail.Done.SetResult(store.ReadDetail(detail.Id));
                        break;
                    case ReadWork read:
                        read.Token.ThrowIfCancellationRequested();
                        var page = store?.Read(read.BeforeId, read.PageSize) ?? new ActivityPage([], null);
                        var missing = Interlocked.Read(ref missed);
                        read.Done.SetResult(page with { Error = store is null ? "活动记录存储不可用：" + writeError : missing == 0 ? null :
                            $"本次后台运行中有 {missing} 条活动未能保存。" + (writeError is null ? "记录较多，请稍后刷新。" : "存储错误：" + writeError) });
                        break;
                    case ClearWork clear:
                        clear.Token.ThrowIfCancellationRequested();
                        if (store is null) throw new InvalidOperationException("活动记录存储不可用，未执行清理。");
                        clear.Done.SetResult(store.ClearBefore(clear.BeforeUtc));
                        break;
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                if (work is ReadWork read) read.Done.TrySetException(error);
                if (work is DetailWork detail) detail.Done.TrySetException(error);
                if (work is ClearWork clear) clear.Done.TrySetException(error);
            }
        }
    }

    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await worker; }
}
