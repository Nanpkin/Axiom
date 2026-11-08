using Axiom.Providers.Windows;

namespace Axiom.Providers.Windows.Tests;

public sealed class WindowsBluetoothDeviceTests
{
    [Fact(Timeout = 25000)]
    public async Task UnpackagedDesktopCanObservePairedEndpointsWithoutOpeningInputDevices()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = WindowsBluetoothDevices.ObserveAsync(["AXIOM SYNTHETIC NONEXISTENT DEVICE"], (values, baseline) =>
        {
            Assert.True(baseline); Assert.False(Assert.Single(values).Connected);
            first.TrySetResult(true);
        }, stop.Token);
        try { await await Task.WhenAny(first.Task, run).WaitAsync(TimeSpan.FromSeconds(18)); }
        finally { stop.Cancel(); try { await run; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } }
    }
}
