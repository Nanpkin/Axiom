using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class BuiltInBrightnessCapabilityProvider(string sid) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [BuiltInBrightnessCapabilities.Set, BuiltInBrightnessAdjustCapabilities.Adjust];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("亮度设置只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        if (invocation.CapabilityId == BuiltInBrightnessAdjustCapabilities.Adjust)
        {
            var adjustment = BuiltInBrightnessAdjustCapabilities.Validate(invocation);
            var adjusted = await WindowsBuiltInBrightness.AdjustAsync(adjustment.Operation, adjustment.Step, sid, token, BrightnessFadeOptions.Parse(invocation.Parameters));
            return (adjusted.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, adjusted.Reason);
        }
        var settings = BuiltInBrightnessCapabilities.Validate(invocation);
        var result = await WindowsBuiltInBrightness.SetAsync(settings.Percent, sid, token, BrightnessFadeOptions.Parse(invocation.Parameters));
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
