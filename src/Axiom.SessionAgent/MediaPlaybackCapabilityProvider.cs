using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class MediaPlaybackCapabilityProvider(Func<IMediaPlaybackSource> connect) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [MediaPlaybackCapabilities.State];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 3 || resources.Any(r => r.CapabilityId != MediaPlaybackCapabilities.State || !MediaPlaybackCapabilities.ValidResource(r.ResourceId)) ||
            resources.Distinct().Count() != resources.Length) throw new ArgumentException("媒体播放状态资源无效。");
        token.ThrowIfCancellationRequested(); IMediaPlaybackSource? source = null;
        publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1, Reason: "正在连接Windows当前媒体状态。"))]));
        try
        {
            source = connect();
            await foreach (var sample in source.Events.ReadAllAsync(token))
            {
                if (source.Failed) throw new IOException("媒体通知失效，丢弃积压状态。");
                publish(new(sample.Baseline, [.. resources.Select(r => new ProviderSample(r, sample.Status == MediaPlaybackStatus.Unknown ? StateQuality.Unknown : StateQuality.Known,
                    "axiom.bool", 1, Boolean: sample.Matches(r.ResourceId), Reason: sample.Reason))]));
            }
            if (!token.IsCancellationRequested) throw new IOException("媒体状态通知结束。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(false, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1, Reason: "媒体状态访问、查询或通知不可用；请暂停并重新启用规则。"))]));
            source?.Dispose(); source = null;
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { source?.Dispose(); }
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("媒体播放状态只能作为检测。");
}
