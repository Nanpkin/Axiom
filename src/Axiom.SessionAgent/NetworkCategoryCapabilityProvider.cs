using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class NetworkCategoryCapabilityProvider(Func<INetworkCategorySource>? connect = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [NetworkCategoryCapabilities.State];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 3 || resources.Any(r => r.CapabilityId != NetworkCategoryCapabilities.State || !NetworkCategoryCapabilities.ValidResource(r.ResourceId)) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.Ordinal).Count() != resources.Length)
            throw new ArgumentException("Windows网络分类资源无效。");
        token.ThrowIfCancellationRequested();
        INetworkCategorySource? source = null;
        publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1, Reason: "正在连接Windows网络分类通知。"))]));
        try
        {
            source = (connect ?? ConnectWindows)();
            await foreach (var sample in source.Events.ReadAllAsync(token))
            {
                if (source.Failed) throw new IOException("Windows 网络分类状态源失效；丢弃积压状态。");
                // Each category has its own Unknown-recovery baseline; do not suppress a known edge in another category.
                foreach (var resource in resources)
                {
                    if (source.Failed) throw new IOException("网络分类通知失效，停止交付此批缓存状态。");
                    var index = NetworkCategorySample.Index(resource.ResourceId); var value = sample.Matches[index];
                    publish(new(sample.Baselines[index], [new(resource, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                        "axiom.bool", 1, Boolean: value, Reason: value.HasValue ? null : sample.Reason)]));
                }
            }
            if (!token.IsCancellationRequested) throw new IOException("Windows 网络分类状态通知中断。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(false, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "Windows网络分类查询或通知不可用；请暂停并重新启用规则。"))]));
            source?.Dispose(); source = null;
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { source?.Dispose(); }
    }
    private static INetworkCategorySource ConnectWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 网络分类状态检测需要Windows。");
        return new WindowsNetworkCategoryEvents();
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("网络分类状态只能作为检测。");
}
