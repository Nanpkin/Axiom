using System.Runtime.Versioning;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

[SupportedOSPlatform("windows")]
internal sealed class AudioCapabilityProvider(Func<WindowsAudioEvents>? connect = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [AudioCapabilities.Changed];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 2 || resources.Any(r => r.CapabilityId != AudioCapabilities.Changed || r.ResourceId is not ("output" or "input"))) throw new ArgumentException("音频检测资源无效。");
        var counts = resources.ToDictionary(r => r.ResourceId, _ => 0m); WindowsAudioEvents? events = null; var snapshotPublished = false;
        try
        {
            events = connect is null ? new WindowsAudioEvents() : connect();
            publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Known, "axiom.event-count", 1, Number: 0))])); snapshotPublished = true;
            await foreach (var kind in events.Events.ReadAllAsync(token))
            {
                var resource = resources.SingleOrDefault(r => r.ResourceId == kind); if (resource is null) continue;
                publish(new(false, [new(resource, StateQuality.Known, "axiom.event-count", 1, Number: ++counts[kind])]));
            }
            if (!token.IsCancellationRequested) throw new IOException("音频通知中断。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            events?.Dispose(); events = null;
            publish(new(!snapshotPublished, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.event-count", 1,
                Reason: "音频通知不可用；请检查Windows音频服务，再暂停并重新启用此规则或重启后台。"))]));
            await Task.Delay(Timeout.Infinite, token); // keep unrelated providers alive; do not retry COM activation in a loop
        }
        finally { events?.Dispose(); }
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("音频变化只能作为检测。");
}
