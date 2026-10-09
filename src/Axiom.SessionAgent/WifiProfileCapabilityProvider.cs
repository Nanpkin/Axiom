using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class WifiProfileCapabilityProvider(
    Func<string[], Action<WifiProfileValue[], bool>, CancellationToken, Task>? observe = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [WifiProfileCapabilities.Connected];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 128 || resources.Any(r => r.CapabilityId != WifiProfileCapabilities.Connected || !WifiProfileCapabilities.ValidName(r.ResourceId)) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.Ordinal).Count() != resources.Length) throw new ArgumentException("Wi-Fi 检测资源无效。");
        var first = true;
        try
        {
            await (observe ?? ObserveWindowsAsync)(resources.Select(r => r.ResourceId).ToArray(), (samples, baseline) =>
            {
                publish(new(first || baseline, [.. samples.Select(s => new ProviderSample(new(WifiProfileCapabilities.Connected, s.ProfileName),
                    s.Connected.HasValue ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1, Boolean: s.Connected,
                    Reason: s.Connected.HasValue ? null : "Wi-Fi 网卡不存在或状态无法核验；不会当作已断开。"))]));
                first = false;
            }, token);
            if (!token.IsCancellationRequested) throw new IOException("Wi-Fi 通知已停止。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(first, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "Wi-Fi 通知不可用；请检查无线网卡及 WLAN AutoConfig 服务，再暂停并重新启用规则。"))]));
            await Task.Delay(Timeout.Infinite, token); // isolate this source; no service-health polling
        }
    }
    private static Task ObserveWindowsAsync(string[] profiles, Action<WifiProfileValue[], bool> publish, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Wi-Fi 检测需要 Windows。");
        return WindowsWifiProfiles.ObserveAsync(profiles, publish, token);
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("Wi-Fi 连接状态只能作为检测。");
}
