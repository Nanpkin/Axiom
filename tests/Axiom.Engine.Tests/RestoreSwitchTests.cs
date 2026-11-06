using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class RestoreSwitchTests
{
    private static readonly IAutomationPlugin[] Modules = BuiltInAutomationPlugins.Create();
    private static readonly AutomationSnapshot Catalog = new([], [.. Modules.SelectMany(m => m.Detections.Select(d => d.Descriptor)
        .Concat(m.Actions.Select(a => a.Descriptor with { SupportsCompositeDetection = true })))],
        [.. Modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, [], CompositeDetectionVersion: 1);
    private static TaskInvocation Defaults(TaskCapability descriptor) => new(descriptor.Id, descriptor.SchemaVersion,
        descriptor.Fields.Where(f => f.DefaultValue is not null).ToImmutableDictionary(f => f.Id, f => f.DefaultValue!));
    private static TaskEditorState Editor(string actionId, string detectionId = "process.running")
    {
        var action = Defaults(Catalog.Capabilities.Single(c => c.Id == actionId));
        if (actionId is ProgramPriorityCapabilities.Set or WindowCapabilities.State)
            action = action with { Parameters = action.Parameters.SetItem("executable", @"C:\Synthetic\Example.exe") };
        var editor = new TaskEditorState();
        editor.Load(new("synthetic-rule", 1, "Synthetic", true,
            new(detectionId, 1, ImmutableDictionary<string, string>.Empty.Add("processes", "example.exe")), action));
        return editor;
    }
    [Fact]
    public void AllEightPairsSwitchToRealDesiredActionsAndBackWithoutLosingSharedValues()
    {
        var restored = Catalog.Capabilities.Where(c => c.OriginalActionId is not null).ToArray();
        Assert.Equal(8, restored.Length);
        foreach (var capability in restored)
        {
            var editor = Editor(capability.OriginalActionId!);
            var original = editor.Action!;
            Assert.True(editor.CanSwitchRestore(Catalog));
            editor.SwitchRestore(Catalog);
            Assert.Equal(capability.Id, editor.Action!.CapabilityId);
            Modules.SelectMany(m => m.Actions).Single(a => a.Descriptor.Id == capability.Id).Compile(editor.Action, ResourceScope.Machine);
            foreach (var pair in editor.Action.Parameters.Where(p => original.Parameters.ContainsKey(p.Key)))
                Assert.Equal(original.Parameters[pair.Key], pair.Value);
            Assert.True(editor.Dirty);
            var saved = JsonSerializer.Deserialize<AutomationTask>(JsonSerializer.Serialize(new AutomationTask("saved", 2, "Synthetic", true,
                editor.Detection!, editor.Action)))!;
            var loaded = new TaskEditorState(); loaded.Load(saved);
            Assert.False(loaded.Dirty);
            Assert.Equal(original.CapabilityId, loaded.RestoreAlternative(Catalog)!.Id);
            loaded.SwitchRestore(Catalog);
            Assert.Equal(original.CapabilityId, loaded.Action!.CapabilityId);
            Assert.Equal(original.Parameters.OrderBy(p => p.Key), loaded.Action.Parameters.OrderBy(p => p.Key));
        }
    }
    [Fact]
    public void EventOrUnsupportedStateCannotEnableRestoreAndKeepsDraft()
    {
        foreach (var detection in new[] { "time.daily", PowerSchemeStateCapabilities.State })
        {
            var editor = Editor(PowerSchemeCapabilities.Set, detection); var before = editor.Action;
            Assert.False(editor.CanSwitchRestore(Catalog));
            Assert.Throws<ArgumentException>(() => editor.SwitchRestore(Catalog));
            Assert.Same(before, editor.Action); Assert.False(editor.Dirty);
        }
    }
    [Theory]
    [InlineData(WindowCapabilities.State, "state", "topmost")]
    [InlineData(WindowCapabilities.State, "title", "Synthetic title")]
    [InlineData(WindowCapabilities.State, "waitSeconds", "10")]
    [InlineData(WindowCapabilities.State, "executable", "C:\\Synthetic\\One.exe\nC:\\Synthetic\\Two.exe")]
    [InlineData(ProgramPriorityCapabilities.Set, "executable", "C:\\Synthetic\\One.exe\nC:\\Synthetic\\Two.exe")]
    public void UnsupportedParametersNeverDisappearSilently(string action, string key, string value)
    {
        var editor = Editor(action); editor.SetParameter(TaskCapabilityKind.Action, key, value);
        var before = editor.Action;
        Assert.Throws<ArgumentException>(() => editor.SwitchRestore(Catalog)); Assert.Same(before, editor.Action);
    }
    [Fact]
    public void CompositeMustSupportRestorationForEveryLeaf()
    {
        var editor = Editor(PowerSchemeCapabilities.Set);
        editor.EnableComposite(Catalog.Capabilities, Catalog.Combinations);
        editor.SelectLeaf([1], Catalog.Capabilities.Single(c => c.Id == GameModeCapabilities.State), Catalog.Combinations);
        var missingPair = Catalog with { Combinations = [.. Catalog.Combinations.Where(c =>
            c != new TaskCombination(GameModeCapabilities.State, ConditionRestoreCapabilities.PowerScheme))] };
        Assert.False(editor.CanSwitchRestore(missingPair));
        Assert.True(editor.CanSwitchRestore(Catalog)); editor.SwitchRestore(Catalog);
        Assert.Equal(ConditionRestoreCapabilities.PowerScheme, editor.Action!.CapabilityId);
    }
    [Fact]
    public void OlderCatalogAndUnsupportedActionsHaveNoPretendRestoreSwitch()
    {
        var editor = Editor(WindowCapabilities.Close);
        Assert.Null(editor.RestoreAlternative(Catalog));
        editor = Editor(BuiltInBrightnessCapabilities.Set);
        var older = Catalog with { Capabilities = [.. Catalog.Capabilities.Select(c => c with { OriginalActionId = null })] };
        Assert.Null(editor.RestoreAlternative(older)); Assert.False(editor.CanSwitchRestore(older));
        Assert.DoesNotContain("OriginalActionId", JsonSerializer.Serialize(Catalog.Capabilities.Single(c => c.Id == WindowCapabilities.Close)));
    }
}
