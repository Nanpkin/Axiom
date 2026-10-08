using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.ServiceHost;

internal interface IUsageObservationSource
{
    Task RunAsync(UsageOptions options, Action ready, Action<UsageSample> publish, CancellationToken token);
}

internal sealed class UsageObservationController(UsageJournal journal, IUsageObservationSource source, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private UsageRecorder? current;
    private CancellationTokenSource? recording;
    private Task worker = Task.CompletedTask;
    private CancellationTokenSource? sessionLifetime;
    private Task expiry = Task.CompletedTask;

    public async Task<AutomationMessage> HandleAsync(AutomationMessage message, AutomationSnapshot catalog, CancellationToken token)
    {
        var request = message.Usage!; UsageValidation.Request(request);
        await operations.WaitAsync(token);
        try
        {
            if (request.Operation == "start")
            {
                lock (gate) if (current is not null && current.Session.State is "recording" or "paused") throw new InvalidOperationException("请先结束当前观察。");
                await StopWorkerAsync(); sessionLifetime?.Cancel();
                current = new(request.Options!, clock.GetUtcNow()); lock (gate) journal.Save(current.Session);
                sessionLifetime?.Dispose(); sessionLifetime = new();
                expiry = ExpireAsync(current.Session.Id, current.Session.EndsUtc, sessionLifetime.Token);
                await StartWorkerAsync();
            }
            else if (request.Operation is "pause" or "stop")
            {
                await StopWorkerAsync();
                lock (gate) if (current is not null)
                {
                    current.State(request.Operation == "pause" && current.Session.State != "stopped" ? "paused" : "stopped", request.Operation == "pause" ? "用户暂停，期间未记录。" : null, clock.GetUtcNow());
                    journal.Save(current.Session);
                }
                if (request.Operation == "stop") sessionLifetime?.Cancel();
            }
            else if (request.Operation == "resume")
            {
                lock (gate)
                {
                    if (current?.Session.State != "paused" || current.Session.EndsUtc <= clock.GetUtcNow()) throw new InvalidOperationException("观察已结束，请新建一次观察。");
                    current.State("recording", atUtc: clock.GetUtcNow()); journal.Save(current.Session);
                }
                await StartWorkerAsync();
            }
            else if (request.Operation is "delete" or "clear")
            {
                if (request.Operation == "clear" || current?.Session.Id == request.SessionId) { await StopWorkerAsync(); sessionLifetime?.Cancel(); current = null; }
                lock (gate) journal.Delete(request.Operation == "clear" ? null : request.SessionId);
            }
            lock (gate)
            {
                var sessions = journal.Read(clock.GetUtcNow());
                var selected = sessions.FirstOrDefault(s => s.Id == request.SessionId) ?? (request.SessionId is null ? sessions.FirstOrDefault() : null);
                if (request.Operation == "feedback")
                {
                    if (selected is null || !UsageAnalysis.Analyze(selected).Any(s => s.Id == request.FeedbackId)) throw new ArgumentException("建议已不存在。");
                    journal.Feedback(selected.Id, UsageAnalysis.FeedbackKey(selected, UsageAnalysis.Analyze(selected).Single(s => s.Id == request.FeedbackId)), request.Text!);
                }
                string? export = null; AutomationTask? draft = null;
                if (request.Operation is "catalog" or "catalog-readable") export = UsageAnalysis.CapabilityCatalog(catalog, request.Operation == "catalog-readable");
                if (request.Operation == "draft")
                {
                    if (selected is null) throw new ArgumentException("记录不存在。");
                    var suggestion = UsageAnalysis.Analyze(selected).Single(s => s.Id == request.FeedbackId);
                    draft = UsageAnalysis.Draft(selected, suggestion, catalog);
                    journal.Feedback(selected.Id, UsageAnalysis.FeedbackKey(selected, suggestion), "accepted");
                }
                if (request.Operation is "export" or "import" or "extension")
                {
                    if (selected is null) throw new ArgumentException("观察已不存在。");
                    if (request.Operation == "export") export = UsageAnalysis.Prompt + UsageAnalysis.Summary(selected, catalog);
                    else
                    {
                        var imported = UsageAnalysis.Import(request.Text ?? "", selected, catalog);
                        if (request.Operation == "extension")
                        {
                            if (imported.Proposal.Kind != "extension") throw new ArgumentException("请选择缺少能力的建议。");
                            export = UsageAnalysis.ExtensionTask(imported.Proposal, catalog);
                        }
                        else { draft = imported.Draft; export = imported.Proposal.Kind + "：" + imported.Proposal.Title + "\n" + imported.Proposal.Explanation; }
                    }
                }
                var dismissed = journal.Dismissed(null);
                var snapshot = new UsageSnapshot([.. sessions.Select(s => s with { Objects = [], Events = [], Options = s.Options with { ExcludedApplications = [] } })],
                    current?.Session.State is "recording" or "paused" ? current.Session.Id : null, clock.GetUtcNow(),
                    selected is null ? null : selected with { Events = [.. selected.Events.Skip(request.Offset).Take(64)] }, selected?.Events.Length ?? 0,
                    selected is null ? [] : [.. UsageAnalysis.Analyze(selected).Where(s => !dismissed.Contains(UsageAnalysis.FeedbackKey(selected, s)))]);
                return new(1, "usage", message.RequestId, Task: draft, UsageSnapshot: snapshot, ExportText: export);
            }
        }
        finally { operations.Release(); }
    }
    public async Task EndLeaseAsync(string id)
    {
        await operations.WaitAsync();
        try
        {
            if (current?.Session.Id != id) return;
            await StopWorkerAsync(); sessionLifetime?.Cancel();
            lock (gate) { current.State("stopped", atUtc: clock.GetUtcNow()); journal.Save(current.Session); }
        }
        finally { operations.Release(); }
    }
    private async Task ExpireAsync(string id, DateTimeOffset at, CancellationToken token)
    {
        try { await Task.Delay(at - clock.GetUtcNow(), clock, token); await EndLeaseAsync(id); }
        catch (OperationCanceledException) { }
    }
    private async Task StartWorkerAsync()
    {
        recording?.Dispose(); recording = new();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker = CaptureAsync(ready, recording.Token);
        await ready.Task;
    }
    private async Task StopWorkerAsync()
    {
        recording?.Cancel(); await worker;
    }
    private async Task CaptureAsync(TaskCompletionSource initialized, CancellationToken token)
    {
        try
        {
            UsageOptions options; lock (gate) options = current!.Session.Options;
            await source.RunAsync(options, () => initialized.TrySetResult(), sample =>
            {
                lock (gate)
                {
                    current!.Add(sample); journal.Save(current.Session);
                    if (current.Session.State == "stopped") recording!.Cancel();
                }
            }, token);
            if (!token.IsCancellationRequested) throw new IOException("观察源意外结束。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { initialized.TrySetCanceled(token); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            initialized.TrySetException(new IOException("无法开启或维持观察；请确认会话后台可用。", error));
            lock (gate) { current!.State("stopped", "观察中断或权限不足，后续未记录；可重新开始观察。"); journal.Save(current.Session); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        sessionLifetime?.Cancel(); await StopWorkerAsync(); await expiry;
        lock (gate) if (current is not null) { current.State("stopped", atUtc: clock.GetUtcNow()); journal.Save(current.Session); }
        recording?.Dispose(); sessionLifetime?.Dispose(); operations.Dispose();
    }
}
