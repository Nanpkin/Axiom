using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class UsbDeviceCapabilityProvider(
    Func<string[], Action<UsbDeviceValue[], bool>, CancellationToken, Task>? observe = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [UsbDeviceCapabilities.Present];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 128 || resources.Any(r => r.CapabilityId != UsbDeviceCapabilities.Present || !UsbDeviceCapabilities.ValidInstanceId(r.ResourceId) || r.ResourceId != r.ResourceId.ToUpperInvariant()) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != resources.Length) throw new ArgumentException("USB 设备检测资源无效。");
        var first = true;
        try
        {
            await (observe ?? ObserveWindowsAsync)(resources.Select(r => r.ResourceId).ToArray(), (samples, baseline) =>
            {
                publish(new(first || baseline, [.. samples.Select(s => new ProviderSample(new(UsbDeviceCapabilities.Present, s.InstanceId),
                    s.Present.HasValue ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1, Boolean: s.Present,
                    Reason: s.Present.HasValue ? null : "USB 实例查询失败或超出容量；不会当作已移除。"))]));
                first = false;
            }, token);
            if (!token.IsCancellationRequested) throw new IOException("USB 通知已停止。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(first, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "USB 原生通知不可用或丢失；请暂停并重新启用规则。"))]));
            await Task.Delay(Timeout.Infinite, token); // isolate failed USB notifications; no health polling
        }
    }
    private static Task ObserveWindowsAsync(string[] instances, Action<UsbDeviceValue[], bool> publish, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("USB 设备检测需要 Windows。");
        return WindowsUsbDevices.ObserveAsync(instances, publish, token);
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("USB 存在状态只能作为检测。");
}
