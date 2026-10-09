using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class DisplayOffCapabilityProvider(string sid) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [DisplayOffCapabilities.Request];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("关闭屏幕请求只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        DisplayOffCapabilities.Validate(invocation);
        var result = await WindowsDisplayOff.RequestAsync(sid, token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
