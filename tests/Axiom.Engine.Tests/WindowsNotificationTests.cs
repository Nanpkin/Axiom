using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsNotificationTests
{
    [Fact(Timeout = 15000)]
    public async Task HideCallbackRemovesIconAndOwnedWindowWithoutRunningContent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = new Shell();
        var run = WindowsDesktopNotification.ShowAsync("test", "https://example.invalid/only-text", false, default, shell);
        var window = await shell.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(IsWindow(window)); Assert.False(shell.Sound);
        Assert.True(PostMessage(window, 0x8001, 1, 0x403));
        var result = await run;
        Assert.True(result.Success); Assert.Contains("不代表", result.Reason);
        Assert.Equal(1, shell.Removed); Assert.False(IsWindow(window));
    }
    [Fact(Timeout = 15000)]
    public async Task CancellationDrainsOwnedWindowAndConcurrentRequestsAreNotQueued()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource(); var shell = new Shell();
        var run = WindowsDesktopNotification.ShowAsync("test", "text", false, cancellation.Token, shell);
        var window = await shell.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var second = new Shell();
            var busy = await WindowsDesktopNotification.ShowAsync("test", "text", false, default, second);
            Assert.False(busy.Success); Assert.Contains("本次未执行", busy.Reason); Assert.False(second.Ready.Task.IsCompleted);
        }
        finally { cancellation.Cancel(); }
        try { await run; } catch (OperationCanceledException) { }
        await WindowsDesktopNotification.DrainAsync();
        Assert.Equal(1, shell.Removed); Assert.False(IsWindow(window));
    }
    [Fact(Timeout = 15000)]
    public async Task RejectedShellRequestStillDestroysMessageWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = new Shell { Accept = false };
        var result = await WindowsDesktopNotification.ShowAsync("test", "text", false, default, shell);
        Assert.False(result.Success); Assert.Equal(0, shell.Removed);
        Assert.False(IsWindow(await shell.Ready.Task));
    }
    private sealed class Shell : IDesktopNotificationShell
    {
        internal readonly TaskCompletionSource<nint> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Accept = true, Sound; internal int Removed;
        public bool Show(nint window, string title, string body, bool sound) { Sound = sound; Ready.SetResult(window); return Accept; }
        public void Remove(nint window) { Interlocked.Increment(ref Removed); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint window, uint message, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
