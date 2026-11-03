using System.IO.Pipes;
using System.Security.Principal;
using Axiom.Contracts;

namespace Axiom.ServiceHost;

internal sealed class GameHdrPipeServer(GameHdrRuntime runtime, string sid, CancellationTokenSource exit)
{
    private CancellationToken stopping => exit.Token;
    private readonly HashSet<Task> clients = [];
    private readonly object gate = new();

    public async Task RunAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            var pipe = LocalPipeFactory.Create(GameHdrProtocol.HostPipe(sid), sid);
            try { await pipe.WaitForConnectionAsync(stopping); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            lock (gate)
            {
                clients.RemoveWhere(x => x.IsCompleted);
                if (clients.Count >= 8) { pipe.Dispose(); continue; }
                clients.Add(Task.Run(() => HandleAsync(pipe)));
            }
        }
        Task[] pending;
        lock (gate) pending = [.. clients];
        await Task.WhenAll(pending);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            GameHdrMessage? request = null;
            var shutdown = false;
            try
            {
                request = await GameHdrProtocol.ReadAsync(pipe, timeout.Token) ?? throw new EndOfStreamException();
                string? clientSid = null;
                pipe.RunAsClient(() => clientSid = WindowsIdentity.GetCurrent().User?.Value);
                if (clientSid != sid) throw new UnauthorizedAccessException("Client SID rejected.");
                GameHdrPipePeer.RequireSameSession(pipe,
                    request.Type is "event" or "hello" ? "Axiom.SessionAgent.exe" : null);
                if (request.Version != 1 || request.Minor != 0 || request.Schema != 1 || request.Scope != "user-session" ||
                    request.RequestId.Length is < 1 or > 64 ||
                    request.DeadlineUtc is not { } deadline || deadline <= DateTimeOffset.UtcNow || deadline > DateTimeOffset.UtcNow.AddSeconds(15))
                    throw new InvalidDataException("Invalid request.");
                if (request.Status is not null || request.Error is not null ||
                    request.Type is "status" or "hello" or "shutdown" && (request.Rule is not null || request.Id is not null || request.Displays is not null || request.ProcessNames is not null) ||
                    request.Type == "event" && (request.Generation is null || request.Displays is { Length: > 32 } || request.Rule is not null || request.Id is not null) ||
                    request.Type == "save" && (request.Rule is null || request.Id is not null || request.Displays is not null) ||
                    request.Type == "delete" && (request.Rule is not null || request.Id is null || request.ExpectedVersion is null || request.Displays is not null))
                    throw new InvalidDataException("Unexpected request fields.");
                GameHdrMessage reply;
                switch (request.Type)
                {
                    case "status":
                        reply = new(1, "status", request.RequestId, Status: await runtime.StatusAsync(timeout.Token));
                        break;
                    case "save" when request.Rule is not null:
                        var saved = await runtime.SaveAsync(request.Rule, timeout.Token);
                        reply = new(1, "saved", request.RequestId, Rule: saved, Status: await runtime.StatusAsync(timeout.Token));
                        break;
                    case "delete" when request.Id is not null && request.ExpectedVersion is not null:
                        await runtime.DeleteAsync(request.Id, request.ExpectedVersion.Value, timeout.Token);
                        reply = new(1, "deleted", request.RequestId, Status: await runtime.StatusAsync(timeout.Token));
                        break;
                    case "event" when request.Rule is null:
                        await runtime.ProcessEventAsync(request, timeout.Token);
                        reply = new(1, "received", request.RequestId);
                        break;
                    case "hello":
                        await runtime.RefreshAgentAsync(timeout.Token);
                        await runtime.MarkAgentConnectionAsync(request.RequestId);
                        reply = new(1, "received", request.RequestId);
                        break;
                    case "shutdown":
                        await runtime.EnsureSafeToStopAsync(timeout.Token);
                        shutdown = true;
                        reply = new(1, "stopping", request.RequestId);
                        break;
                    default: throw new InvalidDataException("Unknown operation.");
                }
                await GameHdrProtocol.WriteAsync(pipe, reply, timeout.Token);
                if (request.Type == "hello")
                {
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    try { await GameHdrProtocol.ReadAsync(pipe, timeout.Token); }
                    finally { await runtime.AgentDisconnectedAsync(request.RequestId); }
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                Console.Error.WriteLine("Game HDR pipe: " + error.Message);
                if (pipe.IsConnected)
                {
                    try { await GameHdrProtocol.WriteAsync(pipe,
                        new(1, "error", request?.RequestId ?? "rejected", Error: error.Message), CancellationToken.None); }
                    catch (IOException) { }
                }
            }
            finally { if (shutdown) exit.Cancel(); }
        }
    }
}

internal sealed class GameHdrExecutorBridge : Axiom.Engine.ICapabilityExecutor, Axiom.Engine.IDesiredReceiptObserver
{
    internal GameHdrRuntime? Runtime;
    public ValueTask<Axiom.Domain.Outcome> ExecuteCommandAsync(Axiom.Domain.Command command, CancellationToken token) =>
        Runtime!.ExecuteCommandAsync(command, token);
    public ValueTask<Axiom.Domain.Outcome> ApplyDesiredAsync(Axiom.Domain.DesiredState desired, CancellationToken token) =>
        Runtime!.ApplyDesiredAsync(desired, token);
    public ValueTask RefreshAfterDesiredReceiptAsync(Axiom.Domain.ResourceKey key, CancellationToken token) =>
        Runtime!.RefreshAfterDesiredReceiptAsync(key, token);
}
