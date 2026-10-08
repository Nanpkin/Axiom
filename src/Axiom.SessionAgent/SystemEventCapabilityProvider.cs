using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class SystemEventCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [SystemEventCapabilities.Event];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Any(r => r.CapabilityId != SystemEventCapabilities.Event || !SystemEventCapabilities.Kinds.Contains(r.ResourceId))) throw new ArgumentException("系统事件资源无效。");
        using var events = new WindowsSessionEvents();
        var counts = resources.ToDictionary(r => r.ResourceId, _ => 0m);
        publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Known, "axiom.event-count", 1, Number: 0))]));
        await foreach (var kind in events.Events.ReadAllAsync(token))
        {
            var resource = resources.SingleOrDefault(r => r.ResourceId == kind); if (resource is null) continue;
            publish(new(false, [new(resource, StateQuality.Known, "axiom.event-count", 1, Number: ++counts[kind])]));
        }
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("系统事件只能作为检测。");
}
