using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[SupportedOSPlatform("windows")]
[Collection("Native application windows")]
public sealed class WindowsWindowStateTests
{
    [Fact(Timeout = 10000)]
    public async Task NativeReadSeparatesHiddenOwnedNormalMinimizedAndMaximizedWindowsWithoutTitles()
    {
        await Task.Yield();
        using var target = new WindowActionTests.TrayWindow(); using var owner = new WindowActionTests.TrayWindow();
        using var api = new WindowsWindowState([Environment.ProcessPath!]);
        Assert.False(api.Read(target.WindowHandle).Candidate);
        target.Show();
        var normal = api.Read(target.WindowHandle); Assert.True(normal.Candidate); Assert.True(normal.IdentityKnown);
        Assert.Equal(Environment.ProcessPath, normal.Executable, ignoreCase: true); Assert.Equal(WindowVisualState.Normal, normal.State);
        target.SetState(3); Assert.Equal(WindowVisualState.Maximized, api.Read(target.WindowHandle).State);
        target.SetState(6); Assert.Equal(WindowVisualState.Minimized, api.Read(target.WindowHandle).State);
        target.SetState(9); target.SetState(9); Assert.Equal(WindowVisualState.Normal, api.Read(target.WindowHandle).State);
        SetWindowLongPtr(target.WindowHandle, -8, owner.WindowHandle);
        try { Assert.False(api.Read(target.WindowHandle).Candidate); }
        finally { SetWindowLongPtr(target.WindowHandle, -8, 0); }
        using var unrelated = new WindowsWindowState([Path.Combine(Path.GetTempPath(), "SyntheticUnrelated.exe")]);
        Assert.False(unrelated.Read(target.WindowHandle).Candidate);
        using var sameName = new WindowsWindowState([Path.Combine(Path.GetTempPath(), "SyntheticOther", Path.GetFileName(Environment.ProcessPath!))]);
        var identity = sameName.Read(target.WindowHandle); Assert.True(identity.IdentityKnown); Assert.Equal(Environment.ProcessPath, identity.Executable, ignoreCase: true);
        var projection = new WindowStateProjection([Path.Combine(Path.GetTempPath(), "SyntheticOther", Path.GetFileName(Environment.ProcessPath!))]);
        projection.Set(target.WindowHandle, identity); Assert.All(projection.Changes(), v => Assert.False(v.Matches));
        target.SetState(0); Assert.False(api.Read(target.WindowHandle).Candidate); Assert.Equal(0, target.CloseCount);
    }

    [Fact(Timeout = 20000)]
    public async Task SharedWinEventsDeliverMinimizeRestoreAfterAnotherClientDisposesAndCanRestartAfterLastRelease()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var target = new WindowActionTests.TrayWindow(); target.Show();
        using (var api = new WindowsWindowState([Environment.ProcessPath!]))
        {
            var sibling = new WindowsWindowEvents(lifecycleOnly: true); sibling.Dispose();
            target.SetState(6); await Confirm(WindowVisualState.Minimized, api);
            target.SetState(9); await Confirm(WindowVisualState.Normal, api);
            target.SetState(3); await Confirm(WindowVisualState.Maximized, api);
        }
        using var restarted = new WindowsWindowState([Environment.ProcessPath!]);
        target.SetState(6); await Confirm(WindowVisualState.Minimized, restarted); Assert.Equal(0, target.CloseCount);

        async Task Confirm(WindowVisualState wanted, WindowsWindowState api)
        {
            await foreach (var window in api.Events.ReadAllAsync(deadline.Token))
                if (window == target.WindowHandle && api.Read(window).State == wanted) return;
            throw new IOException("合成窗口通知提前结束。");
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
