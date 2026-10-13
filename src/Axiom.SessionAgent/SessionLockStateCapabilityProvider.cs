using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class SessionLockStateCapabilityProvider(Func<ISessionLockStateSource>? connect = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [SessionLockStateCapabilities.State];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length != 1 || resources[0].CapabilityId != SessionLockStateCapabilities.State || resources[0].ResourceId != SessionLockStateCapabilities.Resource)
            throw new ArgumentException("会话锁定状态资源无效。");
        token.ThrowIfCancellationRequested(); publish(new(true, [Sample(null, "正在连接当前会话锁定状态。") ]));
        ISessionLockStateSource? source = null;
        try
        {
            source = (connect ?? ConnectWindows)();
            await SessionLockStateObservation.RunAsync(source, sample => publish(new(sample.Baseline,
                [Sample(sample.Locked, sample.Locked.HasValue ? null : "当前活动交互会话或锁定状态无法核验。") ])), token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(false, [Sample(null, "会话锁定通知不可用；请暂停并重新启用规则。") ]));
            source?.Dispose(); source = null; await Task.Delay(Timeout.Infinite, token);
        }
        finally { source?.Dispose(); }
        ProviderSample Sample(bool? value, string? reason) => new(resources[0], value.HasValue ? StateQuality.Known : StateQuality.Unknown,
            "axiom.bool", 1, Boolean: value, Reason: reason);
    }
    private static ISessionLockStateSource ConnectWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("会话锁定状态检测需要Windows。");
        return new WindowsSessionLockState();
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("会话锁定状态只能作为检测。");
}
