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

internal static class SessionCapabilityReadiness
{
    internal static void Require(IEnumerable<string> required, Func<string, bool> hasProvider, bool hasWheelHandler)
    {
        foreach (var id in required)
        {
            // Wheel gestures use their own invocation handler and receipt, outside the generic provider map.
            if (id == BrightnessWheelTool.Command ? hasWheelHandler : hasProvider(id)) continue;
            throw new ArgumentException($"会话程序缺少任务需要的能力：{id}。");
        }
    }
}
