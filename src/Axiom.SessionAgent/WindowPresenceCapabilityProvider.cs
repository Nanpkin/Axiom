using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class WindowPresenceCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [WindowPresenceCapabilities.Visible];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Any(r => r.CapabilityId != WindowPresenceCapabilities.Visible || r.ResourceId.Length is < 5 or > 132 || !r.ResourceId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || r.ResourceId.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\r', '\n']) >= 0)) throw new ArgumentException("窗口检测参数无效。");
        var names = resources.Select(r => r.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var first = true;
        return WindowsWindowPresence.ObserveAsync(names, samples => { publish(new(first, [.. samples.Select(s => new ProviderSample(new(WindowPresenceCapabilities.Visible, s.ProcessName),
            s.Visible.HasValue ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1, Boolean: s.Visible,
            Reason: s.Visible.HasValue ? null : "无法核验可能匹配的窗口身份；不会当作窗口已消失。"))])); first = false; }, token);
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("窗口出现只能作为检测。");
}
