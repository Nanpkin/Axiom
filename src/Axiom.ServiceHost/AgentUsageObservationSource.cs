using Axiom.Contracts;

namespace Axiom.ServiceHost;

internal sealed class AgentUsageObservationSource(string sid) : IUsageObservationSource
{
    public async Task RunAsync(UsageOptions options, Action ready, Action<UsageSample> publish, CancellationToken token)
    {
        using var pipe = LocalUserPipe.Create(ProviderProtocol.PipeName(sid));
        await pipe.ConnectForUserAsync(3000, token);
        var id = Guid.NewGuid().ToString("N");
        await ProviderProtocol.WriteAsync(pipe, new(1, "observe-usage", id, DateTimeOffset.UtcNow.AddSeconds(10), UsageOptions: options), token);
        using var initialization = CancellationTokenSource.CreateLinkedTokenSource(token); initialization.CancelAfter(TimeSpan.FromSeconds(8));
        var first = await ProviderProtocol.ReadAsync(pipe, initialization.Token);
        if (first.Type != "usage-ready" || first.RequestId != id || first.Version != 1) throw new IOException("会话观察未就绪。");
        ready();
        while (!token.IsCancellationRequested)
        {
            var message = await ProviderProtocol.ReadAsync(pipe, token);
            if (message.Type != "usage-event" || message.RequestId != id || message.Version != 1 || message.UsageSample is null) throw new IOException("观察流中断。");
            publish(message.UsageSample);
        }
    }
}
