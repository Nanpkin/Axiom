using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class ForegroundWindowLayoutCapabilityProvider(string sid) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [ForegroundWindowLayoutCapabilities.Layout];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("前台窗口布局只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var layout = ForegroundWindowLayoutCapabilities.Validate(invocation);
        var result = await WindowsForegroundWindowLayout.ApplyAsync(layout, sid, token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
