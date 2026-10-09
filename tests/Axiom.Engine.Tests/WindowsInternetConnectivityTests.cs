using System.Runtime.Versioning;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsInternetConnectivityTests
{
    [Fact(Timeout = 15000)]
    public async Task NativeConnectionPointReadsCurrentReportAndSharedLeasesReleaseIndependently()
    {
        using var one = new WindowsInternetConnectivityEvents(); using var two = new WindowsInternetConnectivityEvents();
        Assert.Equal(2, WindowsInternetConnectivityEvents.ActiveConsumers);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var source in new[] { one, two })
        {
            InternetConnectivitySample sample;
            do { sample = await source.Events.ReadAsync(deadline.Token); } while (!sample.Available.HasValue);
            Assert.True(sample.Baseline); Assert.Null(sample.Reason);
        }
        one.Dispose(); Assert.Equal(1, WindowsInternetConnectivityEvents.ActiveConsumers); Assert.False(two.Events.Completion.IsCompleted);
        two.Dispose(); Assert.Equal(0, WindowsInternetConnectivityEvents.ActiveConsumers);
        // Only subscribes and reads the NLM report; never changes adapters, profiles or NCSI settings.
    }

    [Fact(Timeout = 15000)]
    public async Task ConcurrentNativeLeaseAcquisitionAndReleaseKeepsTheSurvivorSubscribed()
    {
        using var survivor = new WindowsInternetConnectivityEvents();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            using var lease = new WindowsInternetConnectivityEvents(); Assert.False(lease.Events.Completion.IsCompleted);
        })));
        Assert.Equal(1, WindowsInternetConnectivityEvents.ActiveConsumers); Assert.False(survivor.Events.Completion.IsCompleted);
    }
}
