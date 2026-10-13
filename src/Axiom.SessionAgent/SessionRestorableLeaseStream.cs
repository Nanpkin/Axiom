using Axiom.Contracts;
using Axiom.Domain;

namespace Axiom.SessionAgent;

internal static class SessionRestorableLeaseStream
{
    public static async Task RunAsync(Stream pipe, ProviderMessage request, ISessionRestorableCapabilityProvider provider, CancellationToken stopping)
    {
        RestorableSettingCapabilityProvider.Lease? lease = null;
        var released = false;
        try
        {
            using (var acquire = CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                acquire.CancelAfter(request.DeadlineUtc!.Value - DateTimeOffset.UtcNow);
                lease = await provider.AcquireAsync(request.Invocation!, acquire.Token);
                await Reply(request, lease.Initial.Success, lease.Initial.Reason, acquire.Token);
            }
            while (!stopping.IsCancellationRequested)
            {
                var next = await ProviderProtocol.ReadAsync(pipe, stopping);
                ProviderProtocol.Validate(next, DateTimeOffset.UtcNow);
                if (next.RequestId != request.RequestId || next.Sequence != request.Sequence + 1 || next.Invocation?.CapabilityId != request.Invocation!.CapabilityId && next.Type != "lease-setting-release")
                    throw new InvalidDataException("设置租约请求代际或能力无效。");
                request = next;
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                operation.CancelAfter(next.DeadlineUtc!.Value - DateTimeOffset.UtcNow);
                if (next.Type == "lease-setting-update")
                {
                    var result = await lease.UpdateAsync(next.Invocation!, operation.Token);
                    await Reply(next, result.Success, result.Reason, operation.Token);
                }
                else if (next.Type == "lease-setting-release")
                {
                    // The release token must outlive the ended condition/Host pipe.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var result = await lease.ReleaseAsync(cleanup.Token); released = true;
                    await Reply(next, result.Success, result.Reason, operation.Token); return;
                }
                else throw new InvalidDataException("设置租约只接受更新或释放。");
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            if (lease is null && !stopping.IsCancellationRequested)
            {
                using var report = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await Reply(request, false, "本轮目标或已知基线未能确认，未建立设置租约。 " + error.Message, report.Token); }
                catch (Exception writing) when (writing is IOException or OperationCanceledException) { }
            }
        }
        finally { if (lease is not null && !released) await lease.DisposeAsync(); }

        Task Reply(ProviderMessage message, bool success, string reason, CancellationToken token) => ProviderProtocol.WriteAsync(pipe,
            new(1, "lease-setting-result", message.RequestId, Sequence: message.Sequence, Status: success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, Reason: reason), token);
    }
}
