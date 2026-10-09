using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class InternetConnectivityCapabilityProvider(Func<IInternetConnectivitySource>? connect = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [InternetConnectivityCapabilities.State];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length != 1 || resources[0].CapabilityId != InternetConnectivityCapabilities.State || resources[0].ResourceId != InternetConnectivityCapabilities.Resource)
            throw new ArgumentException("Windows Internet状态检测资源无效。");
        token.ThrowIfCancellationRequested();
        var resource = resources[0]; IInternetConnectivitySource? source = null;
        publish(new(true, [new(resource, StateQuality.Unknown, "axiom.bool", 1, Reason: "正在连接Windows Internet状态通知。") ]));
        try
        {
            source = (connect ?? ConnectWindows)();
            await foreach (var sample in source.Events.ReadAllAsync(token))
            {
                if (source.Failed) throw new IOException("Windows Internet状态源失效；丢弃积压状态。");
                publish(new(sample.Baseline, [new(resource, sample.Available.HasValue ? StateQuality.Known : StateQuality.Unknown,
                    "axiom.bool", 1, Boolean: sample.Available, Reason: sample.Reason)]));
            }
            if (!token.IsCancellationRequested) throw new IOException("Windows Internet状态通知中断。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(false, [new(resource, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "Windows Internet状态通知不可用；请检查系统服务，再暂停并重新启用规则。") ]));
            source?.Dispose(); source = null;
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { source?.Dispose(); }
    }
    private static IInternetConnectivitySource ConnectWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Internet状态检测需要Windows。");
        return new WindowsInternetConnectivityEvents();
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("Internet状态只能作为检测。");
}
