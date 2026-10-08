using System.Runtime.Versioning;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsSessionEventTests
{
    [Fact(Timeout = 15000)]
    public async Task ForegroundRuleAndObservationLeasesShareInitialSnapshotAndReleaseIndependently()
    {
        using var rule = new WindowsForegroundEvents(); using var observation = new WindowsForegroundEvents();
        Assert.Equal(2, WindowsForegroundEvents.ActiveConsumers);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.NotNull(await rule.Events.ReadAsync(timeout.Token)); Assert.NotNull(await observation.Events.ReadAsync(timeout.Token));
        observation.Dispose(); Assert.Equal(1, WindowsForegroundEvents.ActiveConsumers); Assert.False(rule.Events.Completion.IsCompleted);
        rule.Dispose(); Assert.Equal(0, WindowsForegroundEvents.ActiveConsumers);
    }
    [Fact(Timeout = 15000)]
    public async Task ConcurrentLeaseAcquisitionAndReleaseKeepsSurvivorAlive()
    {
        using var survivor = new WindowsSessionEvents();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            using var lease = new WindowsSessionEvents(); Assert.False(lease.Events.Completion.IsCompleted);
        })));
        Assert.Equal(1, WindowsSessionEvents.ActiveConsumers); Assert.False(survivor.Events.Completion.IsCompleted);
    }
    [Fact(Timeout = 15000)]
    public async Task RealSharedMessageWindowReleasesOnlyTheCallingLeaseAndCanBeRecreated()
    {
        await Task.Run(() =>
        {
        Assert.Equal(0, WindowsSessionEvents.ActiveConsumers);
        using (var one = new WindowsSessionEvents())
        {
            using var two = new WindowsSessionEvents(); Assert.Equal(2, WindowsSessionEvents.ActiveConsumers);
            one.Dispose(); Assert.Equal(1, WindowsSessionEvents.ActiveConsumers);
            Assert.False(two.Events.Completion.IsCompleted);
        }
        Assert.Equal(0, WindowsSessionEvents.ActiveConsumers);
        using (var again = new WindowsSessionEvents()) Assert.Equal(1, WindowsSessionEvents.ActiveConsumers);
        Assert.Equal(0, WindowsSessionEvents.ActiveConsumers);
        });
    }
}
