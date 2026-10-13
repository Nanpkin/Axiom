using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class TaskbarAutoHideCapabilityProvider(string sid) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [TaskbarAutoHideCapabilities.Set];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("任务栏自动隐藏只能作为动作。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var settings = TaskbarAutoHideCapabilities.Validate(invocation);
        var result = await WindowsTaskbarAutoHide.SetAsync(settings.Enabled, sid, token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
