using Axiom.Contracts;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class UsageWindowTests
{
    [Fact]
    public void InitialWindowsAreBaselinesAndOnlyChangedStatesOrLifecycleProduceRecords()
    {
        var history = new UsageWindowHistory();
        Assert.Null(history.Update(1, "Example.exe", "normal", baseline: true));
        Assert.True(history.Unchanged(1, 0, "normal")); Assert.False(history.Unchanged(1, 2, "normal"));
        Assert.Null(history.Update(1, "Example.exe", "normal"));
        Assert.Equal("window-minimized", history.Update(1, "Example.exe", "minimized")!.Kind);
        Assert.Equal("window-maximized", history.Update(1, "Example.exe", "maximized")!.Kind);
        Assert.Equal("window-normal", history.Update(1, "Example.exe", "normal")!.Kind);
        Assert.Equal("window-hidden", history.Hide(1)!.Kind); Assert.Null(history.Hide(1));
        Assert.Equal("window-shown", history.Update(1, "Other.exe", "normal")!.Kind);
        history.Forget(1); Assert.Null(history.Hide(1));
    }
    [Fact]
    public void WindowHistoryIsBoundedAndReusedHandlesReplaceIdentity()
    {
        var history = new UsageWindowHistory();
        for (var i = 1; i <= 4096; i++) history.Update(i, "Example.exe", "normal", baseline: true);
        Assert.Throws<IOException>(() => history.Update(4097, "Example.exe", "normal"));
        history.Forget(1); Assert.Equal("window-shown", history.Update(4097, "Example.exe", "normal")!.Kind);
        Assert.Equal("Other.exe", history.Update(2, "Other.exe", "maximized")!.Executable);
    }
    [Fact]
    public void WindowScopeIsExplicitAndHiddenDoesNotClaimExitOrUserIntent()
    {
        var at = DateTimeOffset.UtcNow; var path = Path.Combine(Path.GetTempPath(), "Example.exe");
        var recorder = new UsageRecorder(new(5, false, false, [], WindowStates: true), at);
        Assert.True(recorder.Add(new(at, "window-hidden", path)));
        var item = Assert.Single(recorder.Session.Events); Assert.Equal("unknown", item.Origin); Assert.Equal("window-hidden", item.Kind);
        var without = new UsageRecorder(new(5, true, false, []), at); Assert.False(without.Add(new(at, "window-hidden", path)));
        var excluded = new UsageRecorder(new(5, false, false, ["Example.exe"], WindowStates: true), at); Assert.False(excluded.Add(new(at, "window-minimized", path))); Assert.Empty(excluded.Session.Objects);
    }
}
