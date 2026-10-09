using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class OnceTimeCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [OnceTimeCapabilities.Due];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 128 || resources.Any(r => r.CapabilityId != OnceTimeCapabilities.Due)) throw new ArgumentException("定时检测资源无效。");
        var specs = resources.ToDictionary(r => OnceTimeCapabilities.ParseResource(r.ResourceId));
        WindowsSessionEvents? clock = null; var snapshotPublished = false;
        try
        {
            clock = new WindowsSessionEvents(clockOnly: true);
            await OnceEventSource.RunAsync([.. specs.Keys], clock.ClockChanges, (snapshot, values) =>
            {
                publish(new(snapshot, [.. values.Select(value => new ProviderSample(specs[value.Spec], StateQuality.Known,
                    "axiom.event-count", 1, Number: value.Count, Reason: value.Reason))])); snapshotPublished = true;
            }, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            clock?.Dispose(); clock = null;
            publish(new(!snapshotPublished, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.event-count", 1,
                Reason: "定时通知不可用；请检查系统日期和时区，再暂停并重新启用此规则。"))]));
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { clock?.Dispose(); }
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("定时只能作为检测。");
}
