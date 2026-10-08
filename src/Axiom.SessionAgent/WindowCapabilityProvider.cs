using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class WindowCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [WindowCapabilities.Close];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token) =>
        throw new ArgumentException("窗口动作不提供进程检测。");
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var paths = WindowCapabilities.ValidateTargets(invocation);
        var result = await WindowsProgramBatch.ExecuteAsync(paths, (path, cancellation) => WindowsWindowActions.CloseAsync(path, WindowCapabilities.Value(invocation, "title"),
            TimeSpan.FromSeconds((double)WindowCapabilities.Number(invocation, "waitSeconds")), cancellation), token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
