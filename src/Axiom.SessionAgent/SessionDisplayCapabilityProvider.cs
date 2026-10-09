using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class SessionDisplayCapabilityProvider(Func<ISessionDisplaySource>? connect = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [SessionDisplayCapabilities.State];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 3 || resources.Any(r => r.CapabilityId != SessionDisplayCapabilities.State || !SessionDisplayCapabilities.ValidResourceId(r.ResourceId)) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.Ordinal).Count() != resources.Length) throw new ArgumentException("屏幕电源状态资源无效。");
        token.ThrowIfCancellationRequested();
        publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1, Reason: "等待Windows当前会话屏幕电源通知。"))]));
        ISessionDisplaySource? source = null;
        try
        {
            source = (connect ?? ConnectWindows)();
            await SessionDisplayObservation.RunAsync(source.Events, sample => publish(new(sample.Baseline,
                [.. resources.Select(r => new ProviderSample(r, sample.State.HasValue ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1,
                    Boolean: SessionDisplayState.Evaluate(r.ResourceId, sample.State), Reason: sample.State.HasValue ? null : "屏幕状态未知；等待新的Windows通知。"))])), token, () => source.Failed);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(false, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "屏幕电源通知不可用或丢失；请暂停并重新启用规则。"))]));
            source?.Dispose(); source = null;
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { source?.Dispose(); }
    }
    private static ISessionDisplaySource ConnectWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("屏幕电源状态检测需要Windows。");
        return new WindowsSessionDisplay();
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("屏幕电源状态只能作为检测。");
}
