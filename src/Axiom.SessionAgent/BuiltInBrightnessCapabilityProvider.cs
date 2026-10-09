using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class BuiltInBrightnessCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [BuiltInBrightnessCapabilities.Set];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("亮度设置只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var settings = BuiltInBrightnessCapabilities.Validate(invocation);
        var result = await WindowsBuiltInBrightness.SetAsync(settings.Percent, token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
