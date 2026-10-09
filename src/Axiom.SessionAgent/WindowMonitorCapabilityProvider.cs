using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class WindowMonitorCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [WindowMonitorCapabilities.Move];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) => throw new ArgumentException("窗口移屏动作不提供检测。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var result = await WindowsProgramBatch.ExecuteAsync(WindowMonitorCapabilities.ValidateTargets(invocation), (path, cancellation) =>
            WindowsWindowActions.MoveToMonitorAsync(path, WindowMonitorCapabilities.Value(invocation, "title"),
                TimeSpan.FromSeconds((double)WindowMonitorCapabilities.Number(invocation, "waitSeconds")),
                WindowMonitorCapabilities.Value(invocation, "monitor"), cancellation), token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
