using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class WindowOnceCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [WindowOnceCapabilities.Due];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 128 || resources.Any(r => r.CapabilityId != WindowOnceCapabilities.Due)) throw new ArgumentException("时段检测资源无效。");
        var groups = resources.GroupBy(r => WindowOnceCapabilities.ParseResource(r.ResourceId).Spec).ToArray();
        WindowsSessionEvents? clock = null; var snapshotPublished = false;
        try
        {
            clock = new WindowsSessionEvents(clockOnly: true);
            publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Known, "axiom.event-count", 1, Number: 0))])); snapshotPublished = true;
            await WindowOnceEventSource.RunAsync([.. groups.Select(g => g.Key)], clock.ClockChanges, (spec, count) =>
                publish(new(false, [.. groups.Single(g => g.Key == spec).Select(r => new ProviderSample(r, StateQuality.Known, "axiom.event-count", 1, Number: count))])), token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            clock?.Dispose(); clock = null;
            publish(new(!snapshotPublished, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.event-count", 1,
                Reason: "时段通知不可用；请检查系统日期和时区，再暂停并重新启用此任务。"))]));
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { clock?.Dispose(); }
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("时段只能作为检测。");
}
