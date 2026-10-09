using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class EndpointMuteCapabilityProvider(Func<string[], IEndpointMuteSource>? connect = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [EndpointMuteCapabilities.State];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 2 || resources.Any(r => r.CapabilityId != EndpointMuteCapabilities.State || r.ResourceId is not ("input" or "output")) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.Ordinal).Count() != resources.Length)
            throw new ArgumentException("静音检测资源无效。");
        IEndpointMuteSource? source = null;
        // Establish every resource as Unknown before native initialization, which never blocks this snapshot.
        publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1, Reason: "正在连接默认设备静音通知。"))]));
        try
        {
            source = (connect ?? ConnectWindows)([.. resources.Select(r => r.ResourceId)]);
            await foreach (var sample in source.Events.ReadAllAsync(token))
            {
                if (source.Failed) throw new IOException("静音状态源已失效；丢弃积压状态。");
                var resource = resources.SingleOrDefault(r => r.ResourceId == sample.Endpoint);
                if (resource is null) continue;
                publish(new(sample.Baseline, [new(resource, sample.Muted.HasValue ? StateQuality.Known : StateQuality.Unknown,
                    "axiom.bool", 1, Boolean: sample.Muted, Reason: sample.Reason)]));
            }
            if (!token.IsCancellationRequested) throw new IOException("静音通知中断。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(false, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "默认设备静音通知不可用；请检查Windows音频服务，再暂停并重新启用规则或重启后台。"))]));
            source?.Dispose(); source = null;
            await Task.Delay(Timeout.Infinite, token); // Do not spin COM reactivation or interrupt unrelated providers.
        }
        finally { source?.Dispose(); }
    }
    private static IEndpointMuteSource ConnectWindows(string[] endpoints)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("默认设备静音通知需要Windows。");
        return new WindowsEndpointMuteEvents(endpoints);
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("静音状态只能作为检测。");
}
