using Axiom.Contracts;
using Axiom.Domain;

namespace Axiom.SessionAgent;

internal sealed record ProviderUpdate(bool Snapshot, ProviderSample[] Samples);
internal interface ISessionCapabilityProvider
{
    IReadOnlyCollection<string> Capabilities { get; }
    Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token);
    Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token);
}
