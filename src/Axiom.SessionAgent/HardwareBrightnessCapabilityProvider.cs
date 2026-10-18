using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class HardwareBrightnessCapabilityProvider(string sid) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [HardwareBrightnessCapabilities.Set, HardwareBrightnessAdjustCapabilities.Adjust];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("硬件亮度设置只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        if (invocation.CapabilityId == HardwareBrightnessAdjustCapabilities.Adjust)
        {
            var adjustment = HardwareBrightnessAdjustCapabilities.Validate(invocation);
            var adjusted = await WindowsHardwareBrightness.AdjustAsync(adjustment.Target, adjustment.Operation, adjustment.Step, sid, token, BrightnessFadeOptions.Parse(invocation.Parameters));
            return (adjusted.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, adjusted.Reason);
        }
        var settings = HardwareBrightnessCapabilities.Validate(invocation);
        var result = await WindowsHardwareBrightness.SetAsync(settings.Target, settings.Percent, sid, token, BrightnessFadeOptions.Parse(invocation.Parameters));
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
