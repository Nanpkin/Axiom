using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class ForegroundWindowLayoutTests
{
    private static TaskInvocation Invocation(string layout = "left") => new(ForegroundWindowLayoutCapabilities.Layout, 1,
        new Dictionary<string, string> { ["layout"] = layout }.ToImmutableDictionary());

    [Fact]
    public void ActualRegistryIsHotkeyOnlyAndStrictLayoutOnlyImportNeedsNoExecutableOrWindowIdentifier()
    {
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<ForegroundWindowLayoutPlugin>());
        Assert.Equal(new TaskCombination(HotkeyCapabilities.Pressed, ForegroundWindowLayoutCapabilities.Layout), Assert.Single(plugin.Combinations));
        Assert.Equal(RetryCategory.Never, Assert.Single(plugin.Bindings).Descriptor.Retry);
        var action = new ForegroundWindowLayoutTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(action.SupportsTimeWindow); Assert.Single(action.Descriptor.Fields);
        Assert.Equal(action.Compile(Invocation(), scope).Key, action.Compile(Invocation("center"), scope).Key);
        Assert.Equal(7, action.Descriptor.Fields.Single().Choices.Length);
        foreach (var layout in new[] { "maximize", "restore", "next", "LEFT" }) Assert.Throws<ArgumentException>(() => ForegroundWindowLayoutCapabilities.Validate(Invocation(layout)));
        foreach (var field in new[] { "executable", "window", "delaySeconds" })
            Assert.Throws<ArgumentException>(() => ForegroundWindowLayoutCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add(field, "untrusted") }));

        var modules = BuiltInAutomationPlugins.Create();
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        var detection = new TaskInvocation(HotkeyCapabilities.Pressed, 1,
            new Dictionary<string, string> { ["chords"] = "Ctrl+Alt+F9" }.ToImmutableDictionary());
        string Proposal(TaskInvocation command) => JsonSerializer.Serialize(new UsageImport(1, recorder.Session.Id, "existing", "layout", "review", detection, command));
        var draft = UsageAnalysis.Import(Proposal(Invocation()), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Single(draft.Action.Parameters); Assert.Equal("left", draft.Action.Parameters["layout"]);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(Invocation("restore")), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(Invocation() with { Parameters = Invocation().Parameters.Add("window", "123") }), recorder.Session, catalog));
    }

    [Fact]
    public async Task UnknownOrChangedSelectionNeverMovesAnotherWindowAndDestroyedCapturedHandleCannotBeReused()
    {
        var missing = new Window { Current = false };
        Assert.False((await ForegroundWindowLayoutOperation.ApplyAsync(missing, "left", default)).Success); Assert.Empty(missing.Calls);
        var unreadable = new Window { Unknown = true };
        var result = await ForegroundWindowLayoutOperation.ApplyAsync(unreadable, "left", default);
        Assert.False(result.Success); Assert.Contains("未还原或移动", result.Reason); Assert.Empty(unreadable.Calls);
        var switched = new Window(); switched.AfterRestore = () => switched.Current = false;
        result = await ForegroundWindowLayoutOperation.ApplyAsync(switched, "left", default);
        Assert.False(result.Success); Assert.Contains("可能已还原", result.Reason); Assert.Equal(new[] { "restore" }, switched.Calls);
        var beforeBind = new CapturedWindowLifetime(); beforeBind.Destroy(42); beforeBind.Bind(42); Assert.True(beforeBind.Invalid);
        var afterBind = new CapturedWindowLifetime(); afterBind.Destroy(7); afterBind.Bind(42); Assert.False(afterBind.Invalid);
        afterBind.Destroy(7); Assert.False(afterBind.Invalid); afterBind.Destroy(42); Assert.True(afterBind.Invalid);
        var overflow = new CapturedWindowLifetime(); for (var i = 1; i <= 257; i++) overflow.Destroy(i);
        overflow.Bind(999); Assert.True(overflow.Invalid);
    }

    [Fact]
    public async Task EveryLayoutUsesOneRestorePlacementAndReadbackAndPartialFailureNeverRetriesOrRestores()
    {
        foreach (var layout in ForegroundWindowLayoutCapabilities.Descriptor.Fields.Single().Choices.Select(c => c.Value))
        {
            var window = new Window();
            Assert.True((await ForegroundWindowLayoutOperation.ApplyAsync(window, layout, default)).Success);
            Assert.Equal(new[] { "restore", "place:" + layout, "confirm" }, window.Calls);
        }
        var rejected = new Window { PlacementAccepted = false };
        var result = await ForegroundWindowLayoutOperation.ApplyAsync(rejected, "right", default);
        Assert.False(result.Success); Assert.Contains("可能已还原或移动", result.Reason); Assert.Equal(new[] { "restore", "place:right" }, rejected.Calls);
        var mismatch = new Window { Confirmed = false };
        result = await ForegroundWindowLayoutOperation.ApplyAsync(mismatch, "center", default);
        Assert.False(result.Success); Assert.Contains("未读回确认", result.Reason); Assert.Equal(new[] { "restore", "place:center", "confirm" }, mismatch.Calls);
    }

    [Fact]
    public async Task CancellationPreventsLaterWritesAndDoesNotUndoAlreadyRestoredOrMovedWindow()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new Window();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ForegroundWindowLayoutOperation.ApplyAsync(untouched, "left", before.Token)); Assert.Empty(untouched.Calls);
        using var restored = new CancellationTokenSource(); var first = new Window { AfterRestore = restored.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ForegroundWindowLayoutOperation.ApplyAsync(first, "left", restored.Token)); Assert.Equal(new[] { "restore" }, first.Calls);
        using var placed = new CancellationTokenSource(); var second = new Window { AfterPlace = placed.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ForegroundWindowLayoutOperation.ApplyAsync(second, "left", placed.Token)); Assert.Equal(new[] { "restore", "place:left" }, second.Calls);
    }

    private sealed class Window : IForegroundLayoutWindow
    {
        internal bool Current = true, Unknown, PlacementAccepted = true, Confirmed = true;
        internal Action? AfterRestore, AfterPlace;
        internal readonly List<string> Calls = [];
        public bool IsCurrent => Unknown ? throw new UnauthorizedAccessException() : Current;
        public Task<bool> RestoreAsync(CancellationToken token) { Calls.Add("restore"); AfterRestore?.Invoke(); return Task.FromResult(true); }
        public bool Place(string layout, CancellationToken token) { Calls.Add("place:" + layout); AfterPlace?.Invoke(); return PlacementAccepted; }
        public Task<bool> ConfirmAsync(CancellationToken token) { Calls.Add("confirm"); return Task.FromResult(Confirmed); }
    }
}
