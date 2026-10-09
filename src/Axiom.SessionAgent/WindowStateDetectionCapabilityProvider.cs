using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class WindowStateDetectionCapabilityProvider(Func<string[], IWindowStateApi>? connect = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [WindowStateDetectionCapabilities.StateIs];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 128 || resources.Any(r => r.CapabilityId != WindowStateDetectionCapabilities.StateIs) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != resources.Length)
            throw new ArgumentException("窗口状态检测资源无效。");
        var requests = resources.Select(resource => (Resource: resource, Settings: WindowStateDetectionCapabilities.ParseResource(resource.ResourceId))).ToArray();
        var paths = requests.Select(r => r.Settings.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1, Reason: "正在连接窗口状态通知。"))]));
        IWindowStateApi? api = null;
        try
        {
            api = (connect ?? ConnectWindows)(paths);
            await WindowStateObservation.ObserveAsync(api, paths, values =>
            {
                foreach (var value in values)
                {
                    var requested = requests.SingleOrDefault(r => string.Equals(r.Settings.Path, value.Executable, StringComparison.OrdinalIgnoreCase) &&
                        r.Settings.State == value.State.ToString().ToLowerInvariant());
                    if (requested.Resource is null) continue;
                    publish(new(value.Baseline, [new(requested.Resource, value.Matches.HasValue ? StateQuality.Known : StateQuality.Unknown,
                        "axiom.bool", 1, Boolean: value.Matches, Reason: value.Matches.HasValue ? null : "无法核验可能匹配的窗口身份或状态；不会当作没有匹配窗口。") ]));
                }
            }, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            api?.Dispose(); api = null;
            publish(new(false, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "窗口状态通知不可用；请暂停并重新启用规则或重启后台。"))]));
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { api?.Dispose(); }
    }
    private static IWindowStateApi ConnectWindows(string[] paths)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("窗口状态检测需要Windows。");
        return new WindowsWindowState(paths);
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("窗口状态条件只能作为检测。");
}
