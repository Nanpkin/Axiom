using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[SupportedOSPlatform("windows")]
[Collection("Native session observers")]
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
    public async Task PowerOnlySharesSupplyAndPercentageNotificationsAndReleasesWithoutAffectingOtherConsumers()
    {
        using var system = new WindowsSessionEvents(); using var clock = new WindowsSessionEvents(clockOnly: true);
        using var usb = new WindowsSessionEvents(usbOnly: true); using var power = new WindowsSessionEvents(powerOnly: true);
        Assert.Equal(system.NotificationWindow, power.NotificationWindow); Assert.Equal(4, WindowsSessionEvents.ActiveConsumers);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (power.Events.TryRead(out _)) { } // registration may produce an initial setting hint
        SendMessage(power.NotificationWindow, 0x007E, 32, 0);
        while (power.Events.TryRead(out var pending)) Assert.Equal("power-state-changed", pending);
        Assert.Equal("display-changed", await Next(system, "display-changed"));
        var buffer = Marshal.AllocHGlobal(24);
        try
        {
            foreach (var setting in new[] { "A7AD8041-B45A-4CAE-87A3-EECBB468A9E1", "5D3E9A59-E9D5-4B00-A6BD-FF34FF516548" })
            {
                Marshal.StructureToPtr(new Guid(setting), buffer, false); Marshal.WriteInt32(buffer, 16, 4); Marshal.WriteInt32(buffer, 20, 50);
                SendMessage(power.NotificationWindow, 0x0218, 0x8013, buffer);
                Assert.Equal("power-state-changed", await Next(power, "power-state-changed"));
                while (system.Events.TryRead(out var kind)) Assert.NotEqual("power-state-changed", kind);
                Assert.False(usb.Events.TryRead(out _)); Assert.False(clock.Events.TryRead(out _));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        SendMessage(power.NotificationWindow, 0x0218, 0x12, 0);
        Assert.Equal("resumed", await Next(power, "resumed")); Assert.Equal("resumed", await Next(usb, "resumed")); Assert.Equal("resumed", await Next(system, "resumed"));
        Assert.Equal((byte)1, await clock.ClockChanges.ReadAsync(deadline.Token));
        power.Dispose(); Assert.Equal(3, WindowsSessionEvents.ActiveConsumers); Assert.False(system.Events.Completion.IsCompleted);
        SendMessage(system.NotificationWindow, 0x007E, 32, 0); Assert.Equal("display-changed", await Next(system, "display-changed"));
        async Task<string> Next(WindowsSessionEvents source, string expected)
        {
            string value; do { value = await source.Events.ReadAsync(deadline.Token); } while (value != expected);
            return value;
        }
    }

    [Fact(Timeout = 10000)]
    public async Task PowerHintOverflowMarksOnlyPowerConsumerUnknownAndNativeSnapshotRemainsReadOnly()
    {
        using var system = new WindowsSessionEvents(); using var power = new WindowsSessionEvents(powerOnly: true);
        var buffer = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548"), buffer, false);
            Marshal.WriteInt32(buffer, 16, 4); Marshal.WriteInt32(buffer, 20, 1);
            for (var i = 0; i < 65; i++) SendMessage(power.NotificationWindow, 0x0218, 0x8013, buffer);
            Assert.True(power.Failed); Assert.False(system.Failed); Assert.False(system.Events.Completion.IsCompleted);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        var snapshot = await Task.Run(() => WindowsPowerState.Read(default));
        Assert.True(snapshot.AcLine is 0 or 1 or 255);
        Assert.True(snapshot.BatteryPercent <= 100 || snapshot.BatteryPercent == 255);
        // Read-only API coverage; no real supply switch or battery discharge is simulated.
    }

    [Fact(Timeout = 10000)]
    public async Task NativeSetupApiUsbSnapshotReadsBoundedInstancesWithoutRecordingTheirValues()
    {
        using var source = new WindowsSessionEvents(usbOnly: true);
        var instances = await Task.Run(() => WindowsUsbDevices.ReadPresentInstances(default));
        Assert.True(instances.Length <= 256);
        Assert.True(instances.All(id => id.Length is > 0 and <= 255 && !id.Any(char.IsControl)));
        Assert.Equal(instances.Length, instances.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Empty snapshots are valid on runners without exposed USB hardware. This is API coverage, not an unplug test.
    }

    [Fact(Timeout = 10000)]
    public async Task SchemeHintsStayOptInAndOverflowCannotStopExistingConsumers()
    {
        using var system = new WindowsSessionEvents(); using var clock = new WindowsSessionEvents(clockOnly: true);
        using var power = new WindowsSessionEvents(powerOnly: true); using var usb = new WindowsSessionEvents(usbOnly: true);
        using var screen = new WindowsSessionEvents(screenPowerOnly: true); using var scheme = new WindowsSessionEvents(schemeOnly: true);
        Assert.Equal(system.NotificationWindow, scheme.NotificationWindow);
        while (scheme.Events.TryRead(out _)) { }
        var buffer = Marshal.AllocHGlobal(36);
        try
        {
            Marshal.StructureToPtr(new Guid("31F9F286-5084-42FE-B720-2B0264993763"), buffer, false);
            Marshal.WriteInt32(buffer, 16, 16); Marshal.StructureToPtr(new Guid("10000000-0000-0000-0000-000000000001"), buffer + 20, false);
            SendMessage(scheme.NotificationWindow, 0x0218, 0x8013, buffer);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.Equal("power-scheme-changed", await NextContextHint(scheme, "power-scheme-changed", deadline.Token));
            Marshal.WriteInt32(buffer, 16, 4); SendMessage(scheme.NotificationWindow, 0x0218, 0x8013, buffer);
            Assert.Equal("power-scheme-reset", await NextContextHint(scheme, "power-scheme-reset", deadline.Token));
            foreach (var source in new[] { system, power, usb, screen })
                while (source.Events.TryRead(out var kind)) Assert.DoesNotContain("power-scheme", kind);
            Assert.False(clock.Events.TryRead(out _)); Assert.False(clock.ClockChanges.TryRead(out _));
            Marshal.WriteInt32(buffer, 16, 16);
            for (var i = 0; i < 65; i++) SendMessage(scheme.NotificationWindow, 0x0218, 0x8013, buffer);
            Assert.True(scheme.Failed); foreach (var source in new[] { system, clock, power, usb, screen }) Assert.False(source.Failed);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        scheme.Dispose(); Assert.False(system.Events.Completion.IsCompleted); Assert.False(power.Events.Completion.IsCompleted);
        var current = await Task.Run(() => WindowsPowerScheme.ReadActive(default)); Assert.NotEqual(Guid.Empty, current);
        // Only reads and messages to our own window; never changes the real scheme.
    }

    [Fact(Timeout = 10000)]
    public async Task SaverHintsStayOptInAndOverflowCannotStopExistingOrSchemeConsumers()
    {
        using var system = new WindowsSessionEvents(); using var clock = new WindowsSessionEvents(clockOnly: true);
        using var power = new WindowsSessionEvents(powerOnly: true); using var usb = new WindowsSessionEvents(usbOnly: true);
        using var screen = new WindowsSessionEvents(screenPowerOnly: true); using var scheme = new WindowsSessionEvents(schemeOnly: true);
        using var saver = new WindowsSessionEvents(saverOnly: true);
        Assert.Equal(system.NotificationWindow, saver.NotificationWindow); while (saver.Events.TryRead(out _)) { }
        var buffer = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(new Guid("E00958C0-C213-4ACE-AC77-FECCED2EEEA5"), buffer, false);
            Marshal.WriteInt32(buffer, 16, 4); Marshal.WriteInt32(buffer, 20, 1);
            SendMessage(saver.NotificationWindow, 0x0218, 0x8013, buffer);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.Equal("power-saver-changed", await NextContextHint(saver, "power-saver-changed", deadline.Token));
            foreach (var value in new[] { 2, 255, -1 })
            {
                Marshal.WriteInt32(buffer, 20, value); SendMessage(saver.NotificationWindow, 0x0218, 0x8013, buffer);
                Assert.Equal("power-saver-reset", await NextContextHint(saver, "power-saver-reset", deadline.Token));
            }
            Marshal.WriteInt32(buffer, 16, 16); SendMessage(saver.NotificationWindow, 0x0218, 0x8013, buffer);
            Assert.Equal("power-saver-reset", await NextContextHint(saver, "power-saver-reset", deadline.Token));
            foreach (var source in new[] { system, power, usb, screen, scheme })
                while (source.Events.TryRead(out var kind)) Assert.DoesNotContain("power-saver", kind);
            Assert.False(clock.Events.TryRead(out _));
            SendMessage(saver.NotificationWindow, 0x0218, 0x04, 0); Assert.Equal("power-saver-reset", await NextContextHint(saver, "power-saver-reset", deadline.Token));
            SendMessage(saver.NotificationWindow, 0x0218, 0x12, 0); Assert.Equal("resumed", await NextContextHint(saver, "resumed", deadline.Token));
            Assert.Equal((byte)1, await clock.ClockChanges.ReadAsync(deadline.Token));
            Marshal.WriteInt32(buffer, 16, 4); Marshal.WriteInt32(buffer, 20, 0);
            for (var i = 0; i < 65; i++) SendMessage(saver.NotificationWindow, 0x0218, 0x8013, buffer);
            Assert.True(saver.Failed); foreach (var source in new[] { system, clock, power, usb, screen, scheme }) Assert.False(source.Failed);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        saver.Dispose(); Assert.False(system.Events.Completion.IsCompleted); Assert.False(scheme.Events.Completion.IsCompleted);
        var status = await Task.Run(() => WindowsPowerState.ReadSaverStatus(default));
        var sample = PowerSaverStateState.Evaluate("off", status); Assert.Equal(status is 0 or 1, sample.HasValue);
        // Does not require a battery or toggle any real power/energy-saving setting.
    }

    private static async Task<string> NextContextHint(WindowsSessionEvents source, string expected, CancellationToken token)
    {
        string value; do { value = await source.Events.ReadAsync(token); } while (value != expected);
        return value;
    }

    [Fact(Timeout = 15000)]
    public async Task PresenceDwordStaysOptInResetsOnSessionChangeAndOverflowDiscardsItsCacheOnly()
    {
        using var system = new WindowsSessionEvents(); using var clock = new WindowsSessionEvents(clockOnly: true);
        using var power = new WindowsSessionEvents(powerOnly: true); using var usb = new WindowsSessionEvents(usbOnly: true);
        using var screen = new WindowsSessionEvents(screenPowerOnly: true); using var scheme = new WindowsSessionEvents(schemeOnly: true);
        using var presence = new WindowsSessionEvents(presenceOnly: true); using var saver = new WindowsSessionEvents(saverOnly: true);
        Assert.Equal(system.NotificationWindow, presence.NotificationWindow);
        while (presence.Events.TryRead(out _)) { }
        var buffer = Marshal.AllocHGlobal(24); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            Marshal.StructureToPtr(new Guid("3C0F4548-C03F-4C4D-B9F2-237EDE686376"), buffer, false);
            Marshal.WriteInt32(buffer, 16, 4);
            // A real registration/current-state report can interleave; await the expected injected hint, as for scheme/saver.
            foreach (var (raw, expected) in new[] { (0, "presence-present"), (1, "presence-absent"), (2, "presence-inactive"), (3, "presence-reset") })
            {
                Marshal.WriteInt32(buffer, 20, raw); SendMessage(presence.NotificationWindow, 0x0218, 0x8013, buffer);
                Assert.Equal(expected, await NextContextHint(presence, expected, deadline.Token));
            }
            Marshal.WriteInt32(buffer, 16, 0); SendMessage(presence.NotificationWindow, 0x0218, 0x8013, buffer);
            Assert.Equal("presence-reset", await NextContextHint(presence, "presence-reset", deadline.Token));
            foreach (var source in new[] { system, power, usb, screen, scheme, saver })
                while (source.Events.TryRead(out var kind)) Assert.DoesNotContain("presence-", kind);
            Assert.False(clock.ClockChanges.TryRead(out _));
            SendMessage(presence.NotificationWindow, 0x02B1, 2, 0);
            Assert.Equal("presence-reset", await NextContextHint(presence, "presence-reset", deadline.Token));
            Marshal.WriteInt32(buffer, 16, 4); Marshal.WriteInt32(buffer, 20, 1);
            for (var i = 0; i < 65; i++) SendMessage(presence.NotificationWindow, 0x0218, 0x8013, buffer);
            Assert.True(presence.Failed); Assert.False(presence.Events.TryRead(out _));
            foreach (var source in new[] { system, clock, power, usb, screen, scheme, saver }) Assert.False(source.Failed);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        presence.Dispose(); Assert.False(system.Events.Completion.IsCompleted);
        // Only messages to our own window; no user input, session switch, audio or display changes.
    }

    [Fact(Timeout = 15000)]
    public async Task ForegroundCoverReadOnlySharesSourcesAndReleasesItsLeases()
    {
        using var sibling = new WindowsForegroundEvents(); using var session = new WindowsSessionEvents();
        var count = WindowsForegroundEvents.ActiveConsumers; var sessions = WindowsSessionEvents.ActiveConsumers;
        using (var cover = new WindowsForegroundScreenCover())
        {
            Assert.Equal(count + 1, WindowsForegroundEvents.ActiveConsumers); Assert.Equal(sessions + 1, WindowsSessionEvents.ActiveConsumers);
            var reading = cover.Read();
            if (reading.Covers == true) Assert.NotNull(reading.Selection); // true always has a pinned app identity
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await cover.Events.ReadAsync(deadline.Token); // foreground subscription delivers its baseline hint
            Assert.False(cover.Failed);
        }
        Assert.Equal(count, WindowsForegroundEvents.ActiveConsumers); Assert.Equal(sessions, WindowsSessionEvents.ActiveConsumers);
        Assert.False(sibling.Failed); Assert.False(session.Failed);
        // Reads only; never activates, moves, minimizes or maximizes the current user's window.
    }

}
