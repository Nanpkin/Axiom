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
    [Fact(Timeout = 10000)]
    public async Task SyntheticDisplayChangeUsesNativeSharedSourceWithoutRetainingItsPayload()
    {
        using var one = new WindowsSessionEvents(); using var two = new WindowsSessionEvents();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        SendMessage(one.NotificationWindow, 0x007E, 32, (900 << 16) | 1600);
        foreach (var source in new[] { one, two })
        {
            string kind;
            do { kind = await source.Events.ReadAsync(deadline.Token); } while (kind != "display-changed");
            Assert.Equal("display-changed", kind);
        }
    }
    [Fact(Timeout = 10000)]
    public async Task SyntheticUsbClassNotificationsAreSharedWithoutParsingAnyDeviceIdentity()
    {
        using var one = new WindowsSessionEvents(); using var two = new WindowsSessionEvents();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(buffer, 0, 32); Marshal.WriteInt32(buffer, 4, 5); Marshal.WriteInt32(buffer, 8, 0);
            Marshal.StructureToPtr(new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED"), buffer + 12, false);
            Marshal.WriteInt32(buffer, 28, 0); // no path or serial number is required or read
            foreach (var (nativeKind, expected) in new[] { (0x8000, "usb-arrived"), (0x8004, "usb-removed") })
            {
                Assert.Equal((nint)1, SendMessage(one.NotificationWindow, 0x0219, nativeKind, buffer));
                foreach (var source in new[] { one, two })
                {
                    string kind;
                    do { kind = await source.Events.ReadAsync(deadline.Token); } while (!kind.StartsWith("usb-", StringComparison.Ordinal));
                    Assert.Equal(expected, kind);
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [Fact(Timeout = 10000)]
    public async Task ClockOnlyLeaseReceivesRecalculationSignalsWithoutAccumulatingSystemEvents()
    {
        using var system = new WindowsSessionEvents(); using var clock = new WindowsSessionEvents(clockOnly: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        SendMessage(clock.NotificationWindow, 0x001E, 0, 0);
        Assert.Equal((byte)1, await clock.ClockChanges.ReadAsync(deadline.Token)); Assert.False(system.ClockChanges.TryRead(out _));
        SendMessage(clock.NotificationWindow, 0x0218, 0x12, 0);
        Assert.Equal((byte)1, await clock.ClockChanges.ReadAsync(deadline.Token));
        string kind; do { kind = await system.Events.ReadAsync(deadline.Token); } while (kind != "resumed");
        Assert.False(clock.Events.TryRead(out _));
        clock.Dispose(); Assert.Equal(1, WindowsSessionEvents.ActiveConsumers); Assert.False(system.Events.Completion.IsCompleted);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nint w, nint l);

    [Fact(Timeout = 10000)]
    public async Task UsbOnlyLeaseFiltersOtherEventsAndReleasesWithoutStoppingSystemOrClockConsumers()
    {
        using var system = new WindowsSessionEvents(); using var clock = new WindowsSessionEvents(clockOnly: true);
        using var usb = new WindowsSessionEvents(usbOnly: true);
        Assert.Equal(3, WindowsSessionEvents.ActiveConsumers); Assert.Equal(system.NotificationWindow, usb.NotificationWindow);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        SendMessage(usb.NotificationWindow, 0x007E, 32, 0);
        Assert.False(usb.Events.TryRead(out _)); Assert.False(clock.Events.TryRead(out _)); Assert.False(usb.Failed);
        Assert.Equal("display-changed", await Next(system, "display-changed"));
        var buffer = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(buffer, 0, 32); Marshal.WriteInt32(buffer, 4, 5); Marshal.WriteInt32(buffer, 8, 0);
            Marshal.StructureToPtr(new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED"), buffer + 12, false); Marshal.WriteInt32(buffer, 28, 0);
            SendMessage(usb.NotificationWindow, 0x0219, 0x8000, buffer);
            Assert.Equal("usb-arrived", await Next(usb, "usb-arrived")); Assert.Equal("usb-arrived", await Next(system, "usb-arrived"));
            Assert.False(clock.Events.TryRead(out _));
            SendMessage(usb.NotificationWindow, 0x0218, 0x12, 0);
            Assert.Equal("resumed", await Next(usb, "resumed")); Assert.Equal("resumed", await Next(system, "resumed"));
            Assert.Equal((byte)1, await clock.ClockChanges.ReadAsync(deadline.Token));
            usb.Dispose(); Assert.Equal(2, WindowsSessionEvents.ActiveConsumers);
            Assert.False(system.Events.Completion.IsCompleted); Assert.False(clock.ClockChanges.Completion.IsCompleted);
            SendMessage(system.NotificationWindow, 0x0219, 0x8004, buffer);
            Assert.Equal("usb-removed", await Next(system, "usb-removed"));
            SendMessage(clock.NotificationWindow, 0x001E, 0, 0);
            Assert.Equal((byte)1, await clock.ClockChanges.ReadAsync(deadline.Token));
        }
        finally { Marshal.FreeHGlobal(buffer); }
        async Task<string> Next(WindowsSessionEvents source, string expected)
        {
            string value; do { value = await source.Events.ReadAsync(deadline.Token); } while (value != expected);
            return value;
        }
    }

    [Fact(Timeout = 10000)]
    public void NativeSetupApiUsbSnapshotReadsBoundedInstancesWithoutRecordingTheirValues()
    {
        using var source = new WindowsSessionEvents(usbOnly: true);
        var instances = WindowsUsbDevices.ReadPresentInstances(default);
        Assert.True(instances.Length <= 256);
        Assert.True(instances.All(id => id.Length is > 0 and <= 255 && !id.Any(char.IsControl)));
        Assert.Equal(instances.Length, instances.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Empty snapshots are valid on runners without exposed USB hardware. This is API coverage, not an unplug test.
    }

}
