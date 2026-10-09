using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class PowerSchemeCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [PowerSchemeCapabilities.Set];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("电源方案切换只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var settings = PowerSchemeCapabilities.Validate(invocation);
        var result = await WindowsPowerScheme.SetAsync(settings.Scheme, settings.Name, token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
