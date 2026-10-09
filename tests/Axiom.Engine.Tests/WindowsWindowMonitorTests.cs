using System.Runtime.InteropServices;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("Native application windows")]
public sealed class WindowsWindowMonitorTests
{
    [Theory(Timeout = 10000)]
    [InlineData("primary")]
    [InlineData("next")]
    [InlineData("previous")]
    public async Task NativeMoveReadsBackDestinationAndSingleDisplayDirectionPreservesMinimizedState(string direction)
    {
        using var target = new WindowActionTests.TrayWindow(); using var untouched = new WindowActionTests.TrayWindow();
        target.Show(); untouched.Show(); target.SetState(3); target.SetState(6);
        var beforeBounds = Physical(() => target.Bounds); var untouchedBounds = Physical(() => untouched.Bounds);
        var before = WindowsWindowMonitor.ReadMonitors();
        var destination = WindowMonitorGeometry.Select(before, Physical(() => MonitorFromWindow(target.WindowHandle, 2)), direction);
        var result = await WindowsWindowActions.MoveToMonitorAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), direction, default);
        Assert.True(result.Success, result.Reason); Assert.Equal(0, target.CloseCount); Assert.Equal(0, untouched.CloseCount);
        Assert.Equal(untouchedBounds, Physical(() => untouched.Bounds));
        if (before.Length == 1 && direction is "next" or "previous")
        {
            Assert.Contains("只有一个显示器", result.Reason); Assert.Contains("跳过", result.Reason);
            Assert.True(target.Minimized); Assert.Equal(beforeBounds, Physical(() => target.Bounds));
        }
        else
        {
            Assert.False(target.Minimized); Assert.False(target.Maximized); Assert.True(target.Visible);
            var expected = WindowMonitorGeometry.Center(destination); var actual = Physical(() => target.Bounds);
            Assert.Equal((expected.X, expected.Y, expected.X + expected.Width, expected.Y + expected.Height), actual);
            Assert.Equal(destination.Handle, Physical(() => MonitorFromWindow(target.WindowHandle, 2)));
            target.SetState(3); Assert.True(target.Maximized); // A later user change is not reconciled away.
        }
    }

    [Fact(Timeout = 10000)]
    public async Task CancelledWaitAndMissingTitleDoNotMoveOtherWindows()
    {
        using var target = new WindowActionTests.TrayWindow(); using var other = new WindowActionTests.TrayWindow(); other.Show();
        var untouched = Physical(() => other.Bounds); using var cancellation = new CancellationTokenSource();
        var pending = WindowsWindowActions.MoveToMonitorAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), "primary", cancellation.Token);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        target.Show(); Assert.False(target.Maximized); Assert.Equal(0, target.CloseCount);
        Assert.Equal(untouched, Physical(() => other.Bounds));
        var missing = await WindowsWindowActions.MoveToMonitorAsync(Environment.ProcessPath!, "synthetic-missing-" + Guid.NewGuid(), TimeSpan.FromSeconds(1), "primary", default);
        Assert.False(missing.Success); Assert.Contains("超时", missing.Reason); Assert.Equal(untouched, Physical(() => other.Bounds));
    }

    [Fact(Timeout = 10000)]
    public async Task WaitForVisibleWindowMovesOnceAndRejectsWrongCreationIdentityBeforeRestoring()
    {
        using var target = new WindowActionTests.TrayWindow();
        var pending = WindowsWindowActions.MoveToMonitorAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(3), "primary", default);
        target.Show(); var result = await pending; Assert.True(result.Success, result.Reason); Assert.True(target.Visible);
        target.SetState(3); var before = Physical(() => target.Bounds);
        using var events = new WindowsWindowEvents();
        var stale = await WindowsWindowMonitor.ApplyAsync(target.WindowHandle, Environment.ProcessId, Environment.ProcessPath!, DateTime.UnixEpoch, "primary", events, default);
        Assert.False(stale.Success); Assert.Contains("进程已变化", stale.Reason); Assert.True(target.Maximized);
        Assert.Equal(before, Physical(() => target.Bounds));
    }

    [Fact]
    public void UnreadableCreationIdentityIsRejectedWithoutChangingTheWindow()
    {
        using var target = new WindowActionTests.TrayWindow(); target.Show(); target.SetState(3);
        var before = Physical(() => target.Bounds);
        // An unbound Process has no inspectable instance, just as an exited instance cannot be verified.
        using var unavailable = new System.Diagnostics.Process();
        Assert.False(WindowsWindowActions.TryReadCreation(unavailable, out _));
        Assert.True(target.Maximized); Assert.Equal(before, Physical(() => target.Bounds)); Assert.Equal(0, target.CloseCount);
    }

    private static T Physical<T>(Func<T> read)
    {
        var previous = SetThreadDpiAwarenessContext(-4); Assert.NotEqual(0, previous);
        try { return read(); } finally { SetThreadDpiAwarenessContext(previous); }
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
}
