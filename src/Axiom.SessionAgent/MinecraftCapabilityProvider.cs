using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class MinecraftCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [MinecraftTool.Launch, MinecraftTool.Apply];

    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Any(r => r.CapabilityId != MinecraftTool.Launch || !ProcessGroups.ValidProcessName(r.ResourceId)))
            throw new ArgumentException("启动器文件名无效。");
        var targets = resources.ToDictionary(r => r.ResourceId, StringComparer.OrdinalIgnoreCase);
        var counts = resources.ToDictionary(r => r, _ => 0m);
        using var events = new WindowsProcessStartEvents(targets.Keys);
        publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Known, "axiom.event-count", 1, Number: 0))]));
        await foreach (var hint in events.Events.ReadAllAsync(token))
        {
            events.ThrowIfFailed();
            var target = targets[hint.ExecutableName];
            publish(new(false, [new(target, StateQuality.Known, "axiom.event-count", 1, Number: ++counts[target])]));
        }
    }

    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        if (invocation.CapabilityId != MinecraftTool.Apply || invocation.SchemaVersion != 1 || invocation.Parameters.Keys.Any(k => k != "javaPath"))
            throw new ArgumentException("Java 图形设置命令无效。");
        var path = invocation.Parameters.GetValueOrDefault("javaPath");
        if (!string.IsNullOrEmpty(path)) path = MinecraftTool.ValidateJavaPath(path);
        return Task.Run(() =>
        {
            try { return (OutcomeStatus.Succeeded, NvidiaJavaProfile.EnsureNative(path, token)); }
            catch (DllNotFoundException) { return (OutcomeStatus.Failed, "没有找到 NVIDIA x64 驱动接口。"); }
            catch (InvalidOperationException error) { return (OutcomeStatus.Failed, error.Message); }
        }, token);
    }
}
