using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class ApplicationAudioCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [ApplicationAudioCapabilities.Set];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("应用音量只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var settings = ApplicationAudioCapabilities.Validate(invocation);
        var result = await WindowsAudioPlayback.SetApplicationsAsync(settings.Paths, settings.Operation, settings.Percent, token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
