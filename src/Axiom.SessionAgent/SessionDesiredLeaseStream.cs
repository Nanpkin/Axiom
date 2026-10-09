using Axiom.Contracts;
using Axiom.Domain;

namespace Axiom.SessionAgent;

// Called only after CapabilityProviderServer has authenticated the Host and validated the request.
internal static class SessionDesiredLeaseStream
{
    public static async Task RunAsync(Stream pipe, ProviderMessage request, ISessionDesiredCapabilityProvider provider, CancellationToken stopping)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        lifetime.CancelAfter(request.DeadlineUtc!.Value - DateTimeOffset.UtcNow);
        using var lease = provider.AcquireDesired(request.Invocation!, lifetime.Token);
        await ProviderProtocol.WriteAsync(pipe, new(1, "result", request.RequestId,
            Status: OutcomeStatus.Succeeded, Reason: "已持有 Axiom 的保持唤醒请求；条件结束或连接中断后释放。"), lifetime.Token);
        lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
        // The pipe owns the request; no heartbeat or polling is needed.
        try { await ProviderProtocol.ReadAsync(pipe, lifetime.Token); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }
}
