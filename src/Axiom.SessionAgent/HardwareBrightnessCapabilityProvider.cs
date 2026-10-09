using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class HardwareBrightnessCapabilityProvider(string sid) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [HardwareBrightnessCapabilities.Set];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("硬件亮度设置只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var settings = HardwareBrightnessCapabilities.Validate(invocation);
        var result = await WindowsHardwareBrightness.SetAsync(settings.Target, settings.Percent, sid, token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
