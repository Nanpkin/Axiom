using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class OpenTargetBatchCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [OpenTargetBatchCapabilities.Open];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("打开工作目标只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var batch = OpenTargetBatchCapabilities.Validate(invocation);
        var result = await WindowsOpenTarget.OpenBatchAsync(batch.Targets, token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
