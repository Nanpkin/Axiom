using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class ApplicationAudioActivityCapabilityProvider(Func<string[], IApplicationAudioActivitySource> connect) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [ApplicationAudioActivityCapabilities.Active];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 128 || resources.Any(r => r.CapabilityId != ApplicationAudioActivityCapabilities.Active) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != resources.Length) throw new ArgumentException("应用音频检测资源无效。");
        var paths = resources.Select(r => ExecutablePaths.ValidatePath(r.ResourceId)).ToArray();
        publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1, Reason: "正在连接指定应用播放会话通知。"))]));
        IApplicationAudioActivitySource? source = null;
        try
        {
            source = connect(paths);
            await foreach (var sample in source.Events.ReadAllAsync(token))
            {
                if (source.Failed) throw new IOException("应用音频通知失效；丢弃缓存状态。");
                var resource = resources.SingleOrDefault(r => string.Equals(r.ResourceId, sample.Path, StringComparison.OrdinalIgnoreCase));
                if (resource is not null) publish(new(sample.Baseline, [new(resource, sample.Active.HasValue ? StateQuality.Known : StateQuality.Unknown,
                    "axiom.bool", 1, Boolean: sample.Active, Reason: sample.Active.HasValue ? null : "可能匹配的播放会话身份或状态无法核验。") ]));
            }
            if (!token.IsCancellationRequested) throw new IOException("应用音频通知结束。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(false, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1, Reason: "应用音频通知或查询不可用；请暂停并重新启用规则。"))]));
            source?.Dispose(); source = null; await Task.Delay(Timeout.Infinite, token);
        }
        finally { source?.Dispose(); }
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("应用音频活动只能作为检测。");
}
