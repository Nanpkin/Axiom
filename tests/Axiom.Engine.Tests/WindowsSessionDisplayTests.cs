using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

// Injected POWERBROADCAST_SETTING messages test our own HWND path; they never change real screen power.
[SupportedOSPlatform("windows")]
[Collection("Native session observers")]
public sealed class WindowsSessionDisplayTests
{
    [Fact(Timeout = 15000)]
    public async Task LateScreenLeaseReplaysRealPowerReportAndResetAfterTheSharedWindowAlreadyExists()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var general = new WindowsSessionEvents();
        using (var initial = new WindowsSessionEvents(screenPowerOnly: true))
            Assert.StartsWith("screen-", await Next(initial, null, stop.Token));
        // The OS notification window remains alive without a screen consumer.
        SendReport(general.NotificationWindow, 0);
        using var late = new WindowsSessionEvents(screenPowerOnly: true);
        Assert.Equal("screen-off", await Next(late, null, stop.Token));
        SendMessage(general.NotificationWindow, 0x0218, 0x04, 0);
        Assert.Equal("screen-reset", await Next(late, null, stop.Token));
        using var afterReset = new WindowsSessionEvents(screenPowerOnly: true);
        Assert.Equal("screen-reset", await Next(afterReset, null, stop.Token));
        SendReport(general.NotificationWindow, 1);
        Assert.Equal("screen-on", await Next(late, null, stop.Token));
        Assert.Equal("screen-on", await Next(afterReset, null, stop.Token));
    }
    [Fact(Timeout = 15000)]
    public async Task CurrentSessionDisplayRegistrationSharesTheWindowAndDoesNotLeakReportsToOtherConsumers()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var general = new WindowsSessionEvents(); using var topology = new WindowsSessionEvents(displayOnly: true);
        using var screen = new WindowsSessionEvents(screenPowerOnly: true);
        Assert.Equal(general.NotificationWindow, screen.NotificationWindow); Assert.Equal(3, WindowsSessionEvents.ActiveConsumers);
        Drain(general); Drain(topology); Drain(screen);
        SendReport(screen.NotificationWindow, 0); Assert.Equal("screen-off", await Next(screen, "screen-off", stop.Token));
        Assert.All(Drain(general), kind => Assert.DoesNotContain("screen-", kind)); Assert.Empty(Drain(topology));
        SendMessage(screen.NotificationWindow, 0x007E, 32, 0);
        Assert.Equal("display-changed", await Next(topology, "display-changed", stop.Token)); Assert.Empty(Drain(screen));
        SendReport(screen.NotificationWindow, 1); Assert.Equal("screen-on", await Next(screen, "screen-on", stop.Token));
        SendReport(screen.NotificationWindow, 2); Assert.Equal("screen-dim", await Next(screen, "screen-dim", stop.Token));
        SendReport(screen.NotificationWindow, 99); Assert.Equal("screen-reset", await Next(screen, "screen-reset", stop.Token));
        SendReport(screen.NotificationWindow, 0, 3); Assert.Equal("screen-reset", await Next(screen, "screen-reset", stop.Token));
        foreach (var message in new nint[] { 0x04, 0x12 })
        { SendMessage(screen.NotificationWindow, 0x0218, message, 0); Assert.Equal("screen-reset", await Next(screen, "screen-reset", stop.Token)); }
        foreach (var attachment in new nint[] { 1, 2, 3, 4 })
        { SendMessage(screen.NotificationWindow, 0x02B1, attachment, 0); Assert.Equal("screen-reset", await Next(screen, "screen-reset", stop.Token)); }
        Drain(screen); SendMessage(screen.NotificationWindow, 0x02B1, 7, 0);
        Assert.Equal("locked", await Next(general, "locked", stop.Token)); Assert.Empty(Drain(screen));
        screen.Dispose(); Assert.Equal(2, WindowsSessionEvents.ActiveConsumers); Assert.False(general.Events.Completion.IsCompleted);
        SendMessage(general.NotificationWindow, 0x007E, 32, 0); Assert.Equal("display-changed", await Next(topology, "display-changed", stop.Token));
    }
    [Fact(Timeout = 15000)]
    public async Task ASlowScreenConsumerFailsWithoutEndingTheIndependentConsumer()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var slow = new WindowsSessionEvents(screenPowerOnly: true); using var fast = new WindowsSessionEvents(screenPowerOnly: true);
        Drain(slow); Drain(fast);
        for (var i = 0; i < 65; i++)
        {
            SendReport(slow.NotificationWindow, (uint)(i % 3));
            var wanted = (i % 3) switch { 0 => "screen-off", 1 => "screen-on", _ => "screen-dim" };
            Assert.Equal(wanted, await Next(fast, wanted, stop.Token));
        }
        Assert.True(slow.Failed); Assert.False(fast.Failed); Assert.False(fast.Events.Completion.IsCompleted);
        slow.Dispose(); SendReport(fast.NotificationWindow, 1); Assert.Equal("screen-on", await Next(fast, "screen-on", stop.Token));
        Assert.Equal(1, WindowsSessionEvents.ActiveConsumers);
    }
    private static string[] Drain(WindowsSessionEvents events)
    { var list = new List<string>(); while (events.Events.TryRead(out var value)) list.Add(value); return [.. list]; }
    private static async Task<string> Next(WindowsSessionEvents source, string? wanted, CancellationToken token)
    { while (true) { var kind = await source.Events.ReadAsync(token); if (wanted is null || kind == wanted) return kind; } }
    private static void SendReport(nint window, uint value, int length = 4)
    {
        var memory = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5"), memory, false);
            Marshal.WriteInt32(memory, 16, length); Marshal.WriteInt32(memory, 20, unchecked((int)value));
            SendMessage(window, 0x0218, 0x8013, memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nint w, nint l);
}
