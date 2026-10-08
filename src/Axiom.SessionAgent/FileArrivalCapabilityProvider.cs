using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class FileArrivalCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [FileArrivalCapabilities.Arrived];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length < 1 || resources.Any(r => r.CapabilityId != FileArrivalCapabilities.Arrived)) throw new ArgumentException("文件到达检测资源无效。");
        var targets = resources.Select(r => { var spec = FileArrivalCapabilities.ParseResource(r.ResourceId); return new FileArrivalWatch(r.ResourceId, spec.Directory, spec.Kind); }).ToArray();
        var counts = resources.ToDictionary(r => r.ResourceId, _ => 0m); var snapshotPublished = false;
        try
        {
            await FileArrivalSource.RunAsync(targets,
                () => { publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Known, "axiom.event-count", 1, Number: 0))])); snapshotPublished = true; },
                matches => publish(new(false, [.. matches.Select(id => new ProviderSample(new(FileArrivalCapabilities.Arrived, id), StateQuality.Known,
                    "axiom.event-count", 1, Number: ++counts[id]))])), token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(!snapshotPublished, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.event-count", 1,
                Reason: resources.Length > 16 ? "文件检测最多16种目录/类别组合；请暂停部分规则后重试。" : "文件到达通知不可用；请检查目录是否存在及访问权限，再暂停并重新启用此规则。"))]));
            await Task.Delay(Timeout.Infinite, token);
        }
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("文件到达只能作为检测。");
}
