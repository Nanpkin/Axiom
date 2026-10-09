using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class ForegroundWindowMonitorCapabilityProvider(string sid) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [ForegroundWindowMonitorCapabilities.Move];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("前台窗口移屏只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var monitor = ForegroundWindowMonitorCapabilities.Validate(invocation);
        var result = await WindowsForegroundWindowMonitor.MoveAsync(monitor, sid, token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
