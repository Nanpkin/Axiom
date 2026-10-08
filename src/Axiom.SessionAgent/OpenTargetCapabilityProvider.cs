using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class OpenTargetCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [OpenTargetCapabilities.Open];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("打开目标只能作为动作。");
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var target = OpenTargetCapabilities.Validate(invocation);
        var result = WindowsOpenTarget.Open(target.Kind, target.Target, token);
        return Task.FromResult((result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason));
    }
}
