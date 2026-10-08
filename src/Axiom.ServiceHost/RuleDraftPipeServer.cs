using System.IO.Pipes;
using System.Security.Principal;
using Axiom.Contracts;
using Axiom.Engine;

namespace Axiom.ServiceHost;

internal sealed class RuleDraftPipeServer(EngineHost engine, string ownerSid, CancellationToken stopping)
{
    private readonly HashSet<Task> clients = [];
    private readonly object clientGate = new();

    internal async Task RunAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            var pipe = LocalPipeFactory.Create(RuleDraftProtocol.PipeName(ownerSid), ownerSid);
            try { await pipe.WaitForConnectionAsync(stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            lock (clientGate)
            {
                clients.RemoveWhere(task => task.IsCompleted);
                if (clients.Count >= 8) { pipe.Dispose(); continue; }
                clients.Add(Task.Run(() => HandleAsync(pipe)));
            }
        }
        Task[] remaining;
        lock (clientGate) remaining = [.. clients];
        await Task.WhenAll(remaining).ConfigureAwait(false);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            RuleDraftMessage? request = null;
            try
            {
                request = await RuleDraftProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
                if (request is null) return;
                string? requesterSid = null;
                pipe.RunAsClient(() => requesterSid = WindowsIdentity.GetCurrent().User?.Value);
                if (!string.Equals(requesterSid, ownerSid, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Client SID rejected.");
                RuleDraftProtocol.ValidateRequest(request, DateTimeOffset.UtcNow);
                RuleDraftMessage reply;
                if (request.Type == "list")
                {
                    var items = (await engine.ListRuleDraftsAsync(requesterSid!, deadline.Token).ConfigureAwait(false))
                        .Select(x => new RuleDraftSummary(x.Id, x.Version, x.Title, x.UpdatedAtUtc)).ToArray();
                    reply = Reply(request, "list") with { Items = items };
                }
                else if (request.Type == "get")
                {
                    var item = (await engine.ListRuleDraftsAsync(requesterSid!, deadline.Token).ConfigureAwait(false))
                        .FirstOrDefault(x => x.Id == request.Id);
                    reply = item is null ? Reply(request, "error") with { ErrorCode = "NotFound", Error = "规则草稿不存在。" }
                        : Reply(request, "draft") with { Draft = ToDto(item) };
                }
                else
                {
                    var result = await engine.SaveRuleDraftAsync(requesterSid!,
                        new RuleDraftEdit(request.Id!, request.ExpectedVersion, request.Title!,
                            request.ConditionText!, request.ActionText!), deadline.Token).ConfigureAwait(false);
                    reply = result.Status == RuleDraftSaveStatus.Conflict
                        ? Reply(request, "conflict") with { CurrentVersion = result.CurrentVersion, Error = "配置版本已变化，请先重新读取。" }
                        : Reply(request, "saved") with { Draft = ToDto(result.Draft!) };
                }
                await SendAsync(pipe, reply, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                var code = exception switch
                {
                    UnauthorizedAccessException => "Forbidden",
                    InvalidDataException or System.Text.Json.JsonException => "InvalidRequest",
                    ArgumentException => "InvalidDraft",
                    OperationCanceledException => "Timeout",
                    _ => "StorageFailure"
                };
                Console.Error.WriteLine($"Rule draft request failed: {code}: {exception.Message}");
                if (pipe.IsConnected)
                {
                    try
                    {
                        using var sendDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await RuleDraftProtocol.WriteAsync(pipe, new(1, 0, 1, "error", request?.RequestId ?? "rejected",
                            RuleDraftProtocol.Scope, ErrorCode: code,
                            Error: code == "InvalidDraft" ? "规则草稿字段无效。" : "请求未保存：" + code), sendDeadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception writeError) when (writeError is IOException or OperationCanceledException or InvalidOperationException) { }
                }
            }
        }
    }

    private static RuleDraftMessage Reply(RuleDraftMessage request, string type) =>
        new(1, 0, 1, type, request.RequestId, RuleDraftProtocol.Scope);

    private static RuleDraftDto ToDto(RuleDraft draft) => new(draft.Id, draft.Version, draft.Title,
        draft.ConditionText, draft.ActionText, draft.UpdatedAtUtc);

    private static async Task SendAsync(Stream pipe, RuleDraftMessage reply, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await RuleDraftProtocol.WriteAsync(pipe, reply, timeout.Token).ConfigureAwait(false);
    }
}
