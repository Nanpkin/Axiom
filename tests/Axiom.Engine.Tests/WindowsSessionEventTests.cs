using System.Runtime.Versioning;
using System.Runtime.InteropServices;
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
    [Fact(Timeout = 15000)]
    public async Task SyntheticPowerMessagesTraverseNativeWindowAndSharedSubscribers()
    {
        using var rule = new WindowsSessionEvents(); using var observation = new WindowsSessionEvents();
        Assert.Equal(rule.NotificationWindow, observation.NotificationWindow);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        SendPercentage(rule.NotificationWindow, 80);
        SendPercentage(rule.NotificationWindow, 19);
        Assert.Equal("battery-low20", await ReadBattery(rule, timeout.Token));
        Assert.Equal("battery-low20", await ReadBattery(observation, timeout.Token));
        observation.Dispose();
        SendPercentage(rule.NotificationWindow, 9);
        Assert.Equal("battery-low10", await ReadBattery(rule, timeout.Token));

        static async Task<string> ReadBattery(WindowsSessionEvents source, CancellationToken token)
        {
            await foreach (var kind in source.Events.ReadAllAsync(token))
                if (kind.StartsWith("battery-low", StringComparison.Ordinal)) return kind;
            throw new IOException("System notification source ended before the synthetic battery event.");
        }
        static void SendPercentage(nint window, uint value)
        {
            var buffer = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.StructureToPtr(new Guid("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1"), buffer, false);
                Marshal.WriteInt32(buffer, 16, 4); Marshal.WriteInt32(buffer, 20, unchecked((int)value));
                SendMessage(window, 0x0218, 0x8013, buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint w, nint l);

}
