using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class ForegroundScreenCoverCapabilityProvider(Func<IForegroundScreenCoverSource>? connect = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [ForegroundScreenCoverCapabilities.Covers];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length != 1 || resources[0].CapabilityId != ForegroundScreenCoverCapabilities.Covers || resources[0].ResourceId != ForegroundScreenCoverCapabilities.Resource)
            throw new ArgumentException("前台覆盖屏幕检测资源无效。");
        token.ThrowIfCancellationRequested();
        publish(new(true, [Sample(null, "正在连接前台窗口及显示通知。") ]));
        IForegroundScreenCoverSource? source = null;
        try
        {
            source = (connect ?? ConnectWindows)();
            await ForegroundScreenCoverObservation.RunAsync(source, sample => publish(new(sample.Baseline,
                [Sample(sample.Covers, sample.Covers.HasValue ? null : "无法核验当前前台窗口身份、可见边框或显示布局。") ])), token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(false, [Sample(null, "前台窗口或显示通知不可用；请暂停并重新启用规则。") ]));
            source?.Dispose(); source = null;
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { source?.Dispose(); }
        ProviderSample Sample(bool? value, string? reason) => new(resources[0], value.HasValue ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1, Boolean: value, Reason: reason);
    }
    private static IForegroundScreenCoverSource ConnectWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("前台窗口覆盖屏幕检测需要Windows。");
        return new WindowsForegroundScreenCover();
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("前台窗口覆盖屏幕只能作为检测。");
}
