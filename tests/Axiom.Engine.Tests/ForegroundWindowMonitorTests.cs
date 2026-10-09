using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class ForegroundWindowMonitorTests
{
    private static DesktopMonitor Screen(nint handle, int left, bool primary) => new(handle,
        new(left, -200, left + 1000, 800), new(left, -200, left + 1000, 760), primary);
    private static readonly DesktopMonitor Primary = Screen(1, 0, true), Left = Screen(2, -1000, false);
    private static TaskInvocation Invocation(string monitor = "next") => new(ForegroundWindowMonitorCapabilities.Move, 1,
        new Dictionary<string, string> { ["monitor"] = monitor }.ToImmutableDictionary());

    [Fact]
    public void ActualRegistryAndStrictImportProvideOnlyHotkeysAndThreeMonitorChoicesWithoutLocalTargets()
    {
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<ForegroundWindowMonitorPlugin>());
        Assert.Equal(new TaskCombination(HotkeyCapabilities.Pressed, ForegroundWindowMonitorCapabilities.Move), Assert.Single(plugin.Combinations));
        Assert.Equal(RetryCategory.Never, Assert.Single(plugin.Bindings).Descriptor.Retry);
        var action = new ForegroundWindowMonitorTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(action.SupportsTimeWindow); Assert.Single(action.Descriptor.Fields); Assert.Equal(3, action.Descriptor.Fields.Single().Choices.Length);
        Assert.Equal(action.Compile(Invocation(), scope).Key, action.Compile(Invocation("primary"), scope).Key);
        foreach (var invalid in new[] { "device-1", "NEXT", "center" }) Assert.Throws<ArgumentException>(() => ForegroundWindowMonitorCapabilities.Validate(Invocation(invalid)));
        foreach (var field in new[] { "executable", "window", "delaySeconds" })
            Assert.Throws<ArgumentException>(() => ForegroundWindowMonitorCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add(field, "untrusted") }));
        var modules = BuiltInAutomationPlugins.Create();
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        var detection = new TaskInvocation(HotkeyCapabilities.Pressed, 1, new Dictionary<string, string> { ["chords"] = "Ctrl+Alt+F9" }.ToImmutableDictionary());
        string Proposal(TaskInvocation command) => JsonSerializer.Serialize(new UsageImport(1, recorder.Session.Id, "existing", "move", "review", detection, command));
        var draft = UsageAnalysis.Import(Proposal(Invocation()), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Equal("next", Assert.Single(draft.Action.Parameters).Value);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(Invocation("device-1")), recorder.Session, catalog));
    }

    [Fact]
    public async Task SingleScreenAdjacentMovesAreNoopWhilePrimaryAlwaysRestoresAndCentersEvenAlreadyPrimary()
    {
        foreach (var direction in new[] { "next", "previous" })
        {
            var single = new Window { Monitors = [Primary] };
            var result = await ForegroundWindowMonitorOperation.ApplyAsync(single, direction, default);
            Assert.True(result.Success); Assert.Contains("已跳过", result.Reason); Assert.Empty(single.Writes);
        }
        var primary = new Window { Monitors = [Primary] };
        var centered = await ForegroundWindowMonitorOperation.ApplyAsync(primary, "primary", default);
        Assert.True(centered.Success); Assert.Contains("已在主屏也会整理", centered.Reason);
        Assert.Equal(new[] { "restore", "place:1", "confirm" }, primary.Writes); Assert.Equal(WindowMonitorGeometry.Center(Primary), primary.Target);
        foreach (var direction in new[] { "next", "previous" })
        {
            var two = new Window(); Assert.True((await ForegroundWindowMonitorOperation.ApplyAsync(two, direction, default)).Success);
            Assert.Equal(new[] { "restore", "place:2", "confirm" }, two.Writes); Assert.Equal(WindowMonitorGeometry.Center(Left), two.Target);
        }
    }

    [Fact]
    public async Task IdentityAndTopologyUnknownOrChangedRefuseFurtherWritesAndNeverRetargetAnotherWindowOrScreen()
    {
        var unknown = new Window { Unknown = true }; Assert.False((await ForegroundWindowMonitorOperation.ApplyAsync(unknown, "next", default)).Success); Assert.Empty(unknown.Writes);
        var unreadable = new Window { FailTopology = true }; Assert.False((await ForegroundWindowMonitorOperation.ApplyAsync(unreadable, "next", default)).Success); Assert.Empty(unreadable.Writes);
        var missingSource = new Window { Source = 99 }; Assert.False((await ForegroundWindowMonitorOperation.ApplyAsync(missingSource, "primary", default)).Success); Assert.Empty(missingSource.Writes);
        var switched = new Window(); switched.AfterRestore = () => switched.Current = false;
        var result = await ForegroundWindowMonitorOperation.ApplyAsync(switched, "next", default);
        Assert.False(result.Success); Assert.Contains("可能已还原", result.Reason); Assert.Equal(new[] { "restore" }, switched.Writes);
        var removed = new Window(); removed.AfterRestore = () => removed.Monitors = [Primary];
        result = await ForegroundWindowMonitorOperation.ApplyAsync(removed, "next", default);
        Assert.False(result.Success); Assert.Contains("可能已还原", result.Reason); Assert.Equal(new[] { "restore" }, removed.Writes);
    }

    [Fact]
    public async Task RejectedOrUnconfirmedMovesAndCancellationDoNotRetryOrUndoEarlierRestoreOrPlacement()
    {
        var rejected = new Window { Accepted = false };
        var result = await ForegroundWindowMonitorOperation.ApplyAsync(rejected, "next", default);
        Assert.False(result.Success); Assert.Contains("可能已还原或移动", result.Reason); Assert.Equal(new[] { "restore", "place:2" }, rejected.Writes);
        var readback = new Window { Confirmed = false };
        result = await ForegroundWindowMonitorOperation.ApplyAsync(readback, "next", default);
        Assert.False(result.Success); Assert.Contains("未读回确认", result.Reason); Assert.Equal(new[] { "restore", "place:2", "confirm" }, readback.Writes);
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new Window();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ForegroundWindowMonitorOperation.ApplyAsync(untouched, "next", before.Token)); Assert.Empty(untouched.Writes);
        using var afterRestore = new CancellationTokenSource(); var restored = new Window { AfterRestore = afterRestore.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ForegroundWindowMonitorOperation.ApplyAsync(restored, "next", afterRestore.Token)); Assert.Equal(new[] { "restore" }, restored.Writes);
        using var afterPlace = new CancellationTokenSource(); var placed = new Window { AfterPlace = afterPlace.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ForegroundWindowMonitorOperation.ApplyAsync(placed, "next", afterPlace.Token)); Assert.Equal(new[] { "restore", "place:2" }, placed.Writes);
    }

    private sealed class Window : IForegroundMonitorWindow
    {
        internal bool Current = true, Unknown, FailTopology, Accepted = true, Confirmed = true;
        internal nint Source = 1; internal DesktopMonitor[] Monitors = [Primary, Left];
        internal Action? AfterRestore, AfterPlace; internal readonly List<string> Writes = [];
        internal (int X, int Y, int Width, int Height) Target;
        public bool IsCurrent => Unknown ? throw new UnauthorizedAccessException() : Current;
        public DesktopMonitor[] ReadMonitors() => FailTopology ? throw new InvalidOperationException("Unknown") : Monitors;
        public nint ReadCurrentMonitor() => Source;
        public Task<bool> RestoreAsync(DesktopMonitor[] before, CancellationToken token) { Writes.Add("restore"); AfterRestore?.Invoke(); return Task.FromResult(true); }
        public bool Place(DesktopMonitor destination, DesktopMonitor[] before, CancellationToken token)
        { Writes.Add("place:" + destination.Handle); Target = WindowMonitorGeometry.Center(destination); AfterPlace?.Invoke(); return Accepted; }
        public Task<bool> ConfirmAsync(DesktopMonitor[] before, CancellationToken token) { Writes.Add("confirm"); return Task.FromResult(Confirmed); }
    }
}
