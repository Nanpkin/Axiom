using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class DisplayCountCapabilityProvider(Func<string[], Action<DisplayCountValue[], bool>, CancellationToken, Task>? observe = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [DisplayCountCapabilities.Count];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 32 || resources.Any(r => r.CapabilityId != DisplayCountCapabilities.Count || !DisplayCountCapabilities.ValidResourceId(r.ResourceId)) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.Ordinal).Count() != resources.Length) throw new ArgumentException("显示器数量检测资源无效。");
        var first = true;
        try
        {
            await (observe ?? ObserveWindowsAsync)(resources.Select(r => r.ResourceId).ToArray(), (samples, baseline) =>
            {
                publish(new(first || baseline, [.. samples.Select(s => new ProviderSample(new(DisplayCountCapabilities.Count, s.ResourceId),
                    s.Satisfied.HasValue ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1, Boolean: s.Satisfied,
                    Reason: s.Satisfied.HasValue ? null : "Windows未能确认显示器数量；不会当作单屏。"))]));
                first = false;
            }, token);
            if (!token.IsCancellationRequested) throw new IOException("显示布局通知已停止。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(first, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "显示数量通知不可用或丢失；请暂停并重新启用规则。"))]));
            await Task.Delay(Timeout.Infinite, token);
        }
    }
    private static Task ObserveWindowsAsync(string[] resources, Action<DisplayCountValue[], bool> publish, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("显示器数量检测需要Windows。");
        return WindowsDisplayCount.ObserveAsync(resources, publish, token);
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("显示器数量只能作为检测。");
}
