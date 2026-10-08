using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class IntervalCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [IntervalCapabilities.Elapsed];
    public Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 128 || resources.Any(r => r.CapabilityId != IntervalCapabilities.Elapsed)) throw new ArgumentException("计时检测资源无效。");
        var byMinutes = resources.ToDictionary(r => IntervalCapabilities.Parse(r.ResourceId));
        if (byMinutes.Any(p => p.Value.ResourceId != IntervalCapabilities.Resource(p.Key))) throw new ArgumentException("计时资源格式无效。");
        publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Known, "axiom.event-count", 1, Number: 0))]));
        return IntervalEventSource.RunAsync([.. byMinutes.Keys], (minutes, count) => publish(new(false,
            [new(byMinutes[minutes], StateQuality.Known, "axiom.event-count", 1, Number: count)])), token);
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("计时只能作为检测。");
}
