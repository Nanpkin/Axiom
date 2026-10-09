using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class SessionPresenceCapabilityProvider(Func<ISessionPresenceSource>? connect = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [SessionPresenceCapabilities.State];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 3 || resources.Any(r => r.CapabilityId != SessionPresenceCapabilities.State || !SessionPresenceCapabilities.ValidResourceId(r.ResourceId)) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.Ordinal).Count() != resources.Length) throw new ArgumentException("用户在场状态资源无效。");
        token.ThrowIfCancellationRequested();
        publish(new(true, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1, Reason: "等待Windows当前会话用户在场通知。"))]));
        ISessionPresenceSource? source = null;
        try
        {
            source = (connect ?? ConnectWindows)();
            await SessionPresenceObservation.RunAsync(source.Events, sample => publish(new(sample.Baseline,
                [.. resources.Select(r => new ProviderSample(r, sample.State.HasValue ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1,
                    Boolean: SessionPresenceState.Evaluate(r.ResourceId, sample.State), Reason: sample.State.HasValue ? null : "用户在场状态未知；等待新的Windows通知。"))])), token, () => source.Failed);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(false, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "用户在场通知不可用或丢失；请暂停并重新启用规则。"))]));
            source?.Dispose(); source = null;
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { source?.Dispose(); }
    }
    private static ISessionPresenceSource ConnectWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("用户在场状态检测需要Windows。");
        return new WindowsSessionPresence();
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("用户在场状态只能作为检测。");
}
