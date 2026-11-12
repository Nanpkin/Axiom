using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class BluetoothDeviceCapabilityProvider(
    Func<string[], Action<BluetoothDeviceValue[], bool>, CancellationToken, Task> observe) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [BluetoothDeviceCapabilities.Connected];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 128 || resources.Any(r => r.CapabilityId != BluetoothDeviceCapabilities.Connected || !BluetoothDeviceCapabilities.ValidName(r.ResourceId) || r.ResourceId != r.ResourceId.ToUpperInvariant()) ||
            resources.Select(r => r.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != resources.Length) throw new ArgumentException("蓝牙设备检测资源无效。");
        var first = true;
        try
        {
            await observe(resources.Select(r => r.ResourceId).ToArray(), (samples, baseline) =>
            {
                publish(new(first || baseline, [.. samples.Select(s => new ProviderSample(new(BluetoothDeviceCapabilities.Connected, s.Name),
                    s.Connected.HasValue ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1, Boolean: s.Connected,
                    Reason: s.Connected.HasValue ? null : "Windows 蓝牙连接状态尚未确认，不当作断开。"))]));
                first = false;
            }, token);
            if (!token.IsCancellationRequested) throw new IOException("蓝牙通知已停止。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(first, [.. resources.Select(r => new ProviderSample(r, StateQuality.Unknown, "axiom.bool", 1,
                Reason: "Windows 蓝牙连接状态订阅不可用、超时或丢失；请暂停并重新启用规则。"))]));
            await Task.Delay(Timeout.Infinite, token);
        }
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("蓝牙连接状态只能作为检测。");
}
