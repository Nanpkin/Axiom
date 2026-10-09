using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class PowerStateCapabilityProvider(Func<string[], Action<PowerStateValue[], bool>, CancellationToken, Task>? observe = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [PowerStateCapabilities.State];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 101 || resources.Any(r => r.CapabilityId != PowerStateCapabilities.State || !PowerStateCapabilities.ValidResourceId(r.ResourceId)) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.Ordinal).Count() != resources.Length) throw new ArgumentException("供电状态检测资源无效。");
        var first = true;
        try
        {
            await (observe ?? ObserveWindowsAsync)(resources.Select(r => r.ResourceId).ToArray(), (samples, baseline) =>
            {
                publish(new(first || baseline, [.. samples.Select(s => new ProviderSample(new(PowerStateCapabilities.State, s.ResourceId),
                    s.Satisfied.HasValue ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1, Boolean: s.Satisfied,
                    Reason: s.Satisfied.HasValue ? null : "Windows 未能确认供电或电量；不会当作低电量。"))]));
                first = false;
            }, token);
            if (!token.IsCancellationRequested) throw new IOException("供电状态通知已停止。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(first, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "供电状态通知不可用或丢失；请暂停并重新启用规则。"))]));
            await Task.Delay(Timeout.Infinite, token);
        }
    }
    private static Task ObserveWindowsAsync(string[] resources, Action<PowerStateValue[], bool> publish, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("供电状态检测需要 Windows。");
        return WindowsPowerState.ObserveAsync(resources, publish, token);
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("供电状态只能作为检测。");
}
