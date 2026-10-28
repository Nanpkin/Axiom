using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class GameModeCompositionTests
{
    private static TaskInvocation Defaults(TaskCapability capability) => new(capability.Id, capability.SchemaVersion,
        capability.Fields.ToImmutableDictionary(f => f.Id, f => f.DefaultValue ?? ""));
    private static TaskDetectionExpression Leaf(TaskInvocation invocation) => new("leaf", invocation);
    private static TaskDetectionExpression Group(string kind, params TaskDetectionExpression[] children) => new(kind, Children: [.. children]);
    private static AutomationTask TaskFor(TaskDetectionExpression expression) => new(Guid.NewGuid().ToString("D"), 0, "synthetic game condition", false,
        TaskDetectionExpressions.Validate(expression)[0], Defaults(BuiltInBrightnessRestoreCapabilities.Descriptor), 3, DetectionExpression: expression);
    private static (AutomationTaskCompiler Compiler, AutomationSnapshot Catalog) Registered()
    {
        var modules = BuiltInAutomationPlugins.Create();
        var compiler = new AutomationTaskCompiler(supportsCompositeDetection: true);
        foreach (var module in modules)
        {
            foreach (var detection in module.Detections) compiler.Register(detection);
            foreach (var action in module.Actions) compiler.Register(action);
            foreach (var combination in module.Combinations) compiler.RegisterCombination(combination);
        }
        return (compiler, new([], compiler.Catalog, [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UnixEpoch, [], CompositeDetectionVersion: 1));
    }
    private static TruthValue Evaluate(RuleCondition condition, bool? game, bool? bright)
    {
        var source = new SourceSession("synthetic-game", 1);
        var states = condition.Dependencies.ToDictionary(k => k, k =>
        {
            var value = k.CapabilityId == GameModeCapabilities.State ? game : bright;
            return new ObservedState(new(k, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                value.HasValue ? StateValue.From(new("axiom.bool", 1), value.Value) : null, DateTimeOffset.UnixEpoch), source, 1, 1);
        });
        return RuleEvaluator.Evaluate(condition, states).Value;
    }

    [Fact]
    public void BrightScreenAndNotGamingPreserveUnknownAndSupportRestoringBrightness()
    {
        var (compiler, _) = Registered();
        var bright = Leaf(Defaults(ScreenContentCapabilities.Descriptor));
        var game = Leaf(Defaults(GameModeCapabilities.Descriptor));
        var rule = compiler.Compile(TaskFor(Group("all", bright, Group("not", game))), "synthetic-owner", ResourceScope.Machine);
        Assert.Equal(RuleActionKind.Desired, rule.Action.Kind);
        Assert.Equal(2, rule.Condition.Dependencies.Count);
        Assert.Equal(TruthValue.True, Evaluate(rule.Condition, false, true));
        Assert.Equal(TruthValue.False, Evaluate(rule.Condition, true, true));
        Assert.Equal(TruthValue.Unknown, Evaluate(rule.Condition, null, true));
        Assert.Equal(TruthValue.Unknown, Evaluate(rule.Condition, false, null));
        Assert.Equal(TruthValue.False, Evaluate(rule.Condition, null, false));
        var either = compiler.Compile(TaskFor(Group("any", bright, game)), "synthetic-owner", ResourceScope.Machine).Condition;
        Assert.Equal(TruthValue.True, Evaluate(either, true, null));
        Assert.Equal(TruthValue.Unknown, Evaluate(either, null, false));
        var off = Defaults(GameModeCapabilities.Descriptor) with { Parameters = Defaults(GameModeCapabilities.Descriptor).Parameters.SetItem("state", "off") };
        var explicitOff = compiler.Compile(TaskFor(Group("all", bright, Leaf(off))), "synthetic-owner", ResourceScope.Machine).Condition;
        Assert.Equal(TruthValue.True, Evaluate(explicitOff, false, true));
        Assert.Equal(TruthValue.False, Evaluate(explicitOff, true, true));
        Assert.Equal(TruthValue.Unknown, Evaluate(explicitOff, null, true));
    }

    [Fact]
    public void EditorCanAddGameModeLeafNegateItAndSaveAllParameters()
    {
        var (compiler, catalog) = Registered();
        var original = new AutomationTask(Guid.NewGuid().ToString("D"), 5, "synthetic bright screen", false,
            Defaults(ScreenContentCapabilities.Descriptor), Defaults(BuiltInBrightnessRestoreCapabilities.Descriptor));
        var editor = new TaskEditorState(); editor.Load(original);
        editor.EnableComposite(catalog.Capabilities, catalog.Combinations);
        editor.SelectLeaf([1], catalog.Capabilities.Single(c => c.Id == GameModeCapabilities.State), catalog.Combinations);
        editor.ToggleNot([1]);
        Assert.True(editor.CanSave(catalog));
        var saved = JsonSerializer.Deserialize<AutomationTask>(JsonSerializer.Serialize(editor.Build()))!;
        editor.Load(saved);
        Assert.False(editor.Dirty);
        Assert.True(editor.CanSave(catalog));
        Assert.True(TaskDetectionExpressions.SameInvocation(original.Action, saved.Action));
        Assert.True(TaskDetectionExpressions.SameInvocation(original.Detection, saved.Detection));
        Assert.Equal("not", saved.DetectionExpression!.Children[1].Kind);
        Assert.Equal("on", TaskDetectionExpressions.ValidateTask(saved)[1].Parameters["state"]);
        Assert.Equal(TruthValue.False, Evaluate(compiler.Compile(saved, "synthetic-owner", ResourceScope.Machine).Condition, true, true));
    }

    [Fact]
    public void ExistingScheduledBrightnessTasksKeepOnlyTheirOriginalTimeDependency()
    {
        var (compiler, _) = Registered();
        foreach (var (time, brightness, parameter) in new[]
        {
            (WindowOnceCapabilities.Descriptor, HardwareBrightnessCapabilities.Descriptor, "startHour"),
            (DailyTimeCapabilities.Descriptor, BuiltInBrightnessCapabilities.Descriptor, "hour")
        })
        foreach (var hour in new[] { "8", "22" })
        {
            var detection = Defaults(time);
            detection = detection with { Parameters = detection.Parameters.SetItem(parameter, hour) };
            var original = new AutomationTask(Guid.NewGuid().ToString("D"), 2, "synthetic scheduled brightness", false,
                detection, Defaults(brightness));
            var editor = new TaskEditorState(); editor.Load(original);
            var saved = editor.Build();
            Assert.Null(saved.DetectionExpression);
            Assert.True(TaskDetectionExpressions.SameInvocation(original.Detection, saved.Detection));
            var rule = compiler.Compile(saved, "synthetic-owner", ResourceScope.Machine);
            Assert.Equal(RuleActionKind.Command, rule.Action.Kind);
            Assert.Equal(time.Id, Assert.Single(rule.Condition.Dependencies).CapabilityId);
        }
    }
}
