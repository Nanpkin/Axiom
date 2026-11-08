using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class TrayCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [TrayCapabilities.Hide, TrayCapabilities.Show];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) =>
        throw new ArgumentException("托盘动作不提供进程检测。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var paths = TrayCapabilities.ValidateTargets(invocation);
        var wait = TimeSpan.FromSeconds((double)TrayCapabilities.Number(invocation, "waitSeconds"));
        var result = await WindowsProgramBatch.ExecuteAsync(paths, (path, cancellation) => invocation.CapabilityId == TrayCapabilities.Show
            ? WindowsTrayActions.ShowAsync(path, wait, cancellation) : WindowsTrayActions.HideAsync(path, wait, cancellation), token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
