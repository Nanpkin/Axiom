using System.Runtime.Versioning;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsSessionEventTests
{
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
