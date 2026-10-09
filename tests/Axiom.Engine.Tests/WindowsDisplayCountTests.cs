using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[SupportedOSPlatform("windows")]
[Collection("Native session observers")]
public sealed class WindowsDisplayCountTests
{
    [Fact]
    public void NativeEnumerationReadsABoundedLogicalDesktopCountWithoutChangingDisplaySettings()
    {
        Assert.InRange(Assert.IsType<int>(WindowsDisplayCount.Read()), 1, DisplayCountState.MaximumMonitors);
    }
    [Fact(Timeout = 15000)]
    public async Task DisplayLeaseSharesTheExistingWindowFiltersOtherHintsAndReleasesIndependently()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var system = new WindowsSessionEvents(); using var display = new WindowsSessionEvents(displayOnly: true);
        Assert.Equal(system.NotificationWindow, display.NotificationWindow); Assert.Equal(2, WindowsSessionEvents.ActiveConsumers);
        SendMessage(display.NotificationWindow, 0x02B1, 7, 0);
        Assert.Equal("locked", await Next(system, "locked", stop.Token)); Assert.False(display.Events.TryRead(out _));
        SendMessage(display.NotificationWindow, 0x007E, 32, 0);
        Assert.Equal("display-changed", await Next(display, "display-changed", stop.Token));
        SendMessage(display.NotificationWindow, 0x0218, 0x12, 0);
        Assert.Equal("resumed", await Next(display, "resumed", stop.Token));
        display.Dispose(); Assert.Equal(1, WindowsSessionEvents.ActiveConsumers); Assert.False(system.Events.Completion.IsCompleted);
        SendMessage(system.NotificationWindow, 0x007E, 32, 0); Assert.Equal("display-changed", await Next(system, "display-changed", stop.Token));
        system.Dispose(); Assert.Equal(0, WindowsSessionEvents.ActiveConsumers);
    }
    private static async Task<string> Next(WindowsSessionEvents events, string wanted, CancellationToken token)
    {
        while (true) { var kind = await events.Events.ReadAsync(token); if (kind == wanted) return kind; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nint w, nint l);
}
