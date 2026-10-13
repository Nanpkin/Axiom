using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Domain;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class CompositeEditorTests
{
    private static readonly TaskCapability State = new("synthetic.state", 1, TaskCapabilityKind.Detection, "状态", "合成状态", "",
        [new("value", "状态", TaskFieldKind.Text, true, "", "known", 16, [])], "", "", IsStateCondition: true);
    private static readonly TaskCapability Other = State with { Id = "synthetic.other" };
    private static readonly TaskCapability Action = new("synthetic.action", 1, TaskCapabilityKind.Action, "动作", "合成动作", "",
        [new("target", "目标", TaskFieldKind.Text, true, "", "retained", 16, [])], "", "", SupportsCompositeDetection: true);
    private static readonly ImmutableArray<TaskCapability> Catalog = [State, Other, Action];
    private static readonly ImmutableArray<TaskCombination> Pairs = [new(State.Id, Action.Id), new(Other.Id, Action.Id)];
    private static TaskInvocation Invocation(string id, string value) => new(id, 1, ImmutableDictionary<string, string>.Empty.Add("value", value));
    private static AutomationTask Single(WeeklyTimeWindow? window = null) => new(Guid.NewGuid().ToString("D"), 7, "保留旧规则", true,
        Invocation(State.Id, "original"), new(Action.Id, 1, ImmutableDictionary<string, string>.Empty.Add("target", "retained")),
        SchemaVersion: window is null ? 1 : 2, TimeWindow: window);

    [Fact]
    public void SingleSchemasAndImportedDraftKeepParametersTimeWindowAndUnsavedProtection()
    {
        foreach (var window in new WeeklyTimeWindow?[] { null, new(540, 1080, 62) })
        {
            var task = Single(window); var editor = new TaskEditorState(); editor.Load(task);
            Assert.False(editor.Dirty); Assert.True(editor.Complete); Assert.Null(editor.Build().DetectionExpression);
            Assert.Equal(task.SchemaVersion, editor.Build().SchemaVersion); Assert.Equal(window, editor.Build().TimeWindow);
            Assert.True(TaskDetectionExpressions.SameInvocation(task.Detection, editor.Build().Detection));
            editor.EnableComposite(Catalog, Pairs); Assert.True(editor.Dirty); Assert.False(editor.Complete);
            Assert.Equal("original", editor.Node([0]).Invocation!.Parameters["value"]);
            Assert.Throws<ArgumentException>(() => editor.DisableComposite());
            editor.Remove([1]); editor.DisableComposite();
            Assert.False(editor.Dirty); Assert.Equal(task.Version, editor.Build().Version); Assert.True(editor.Build().Paused);
            editor.LoadDraft(task with { Version = 0 }); Assert.True(editor.Dirty); Assert.Equal(0, editor.Build().Version);
            editor.EnableComposite(Catalog, Pairs); editor.Remove([]); editor.DisableComposite();
            Assert.Null(editor.DetectionExpression); Assert.False(editor.Complete);
            Assert.Equal("retained", editor.Action!.Parameters["target"]); Assert.Equal(task.Title, editor.Title); Assert.Equal(window, editor.TimeWindow);
        }
    }

    [Fact]
    public void NestedAllAnyNotEditReorderAndSerializationKeepEveryLeafAndFirstAnchor()
    {
        var editor = new TaskEditorState(); editor.Load(Single(new(540, 1080, 62)));
        editor.EnableComposite(Catalog, Pairs); editor.SelectLeaf([1], Other, Pairs); editor.SetLeafParameter([1], "value", "second");
        editor.ToggleNot([1]); editor.AddGroup([]); editor.ChangeGroup([2], "any");
        editor.SelectLeaf([2, 0], State, Pairs); editor.SetLeafParameter([2, 0], "value", "third"); editor.SelectLeaf([2, 1], Other, Pairs);
        Assert.Equal("original", editor.Node([0]).Invocation!.Parameters["value"]);
        editor.Move([0], 1);
        var task = editor.Build(); Assert.Equal(3, task.SchemaVersion); Assert.Equal("all", task.DetectionExpression!.Kind);
        var leaves = TaskDetectionExpressions.ValidateTask(task); Assert.Equal(4, leaves.Length); Assert.Equal("second", task.Detection.Parameters["value"]);
        Assert.Contains(leaves, leaf => leaf.Parameters["value"] == "third"); Assert.True(editor.Dirty);
        var loaded = JsonSerializer.Deserialize<AutomationTask>(JsonSerializer.Serialize(task))!;
        editor.Load(loaded); Assert.False(editor.Dirty); Assert.True(editor.Complete); Assert.Equal(loaded.TimeWindow, editor.Build().TimeWindow);
        // Moving between two populated groups must preserve all parameters and update the legacy display anchor.
        editor.ChangeGroup([0], "any"); editor.SelectLeaf([0, 1], State, Pairs); editor.AddLeaf([0]); editor.SelectLeaf([0, 2], Other, Pairs);
        editor.MoveToGroup([0, 0], [2]);
        var moved = editor.Build(); Assert.Equal(6, TaskDetectionExpressions.ValidateTask(moved).Length);
        Assert.Equal("known", moved.Detection.Parameters["value"]);
        Assert.True(TaskDetectionExpressions.SameInvocation(editor.Node([0, 0]).Invocation!, moved.Detection));
        Assert.Contains(TaskDetectionExpressions.ValidateTask(moved), leaf => leaf.Parameters["value"] == "original");
        Assert.Equal("second", editor.Node([2, 2]).Invocation!.Parameters["value"]);
        Assert.Throws<ArgumentException>(() => editor.MoveToGroup([2], [2, 0]));
    }

    [Fact]
    public void MetadataFeedbackAndBoundsRejectChangesAtomicallyWithoutDroppingInput()
    {
        var editor = new TaskEditorState(); editor.Load(Single()); editor.EnableComposite(Catalog, Pairs); editor.SelectLeaf([1], Other, Pairs);
        var unsupportedAction = Action with { SupportsCompositeDetection = false };
        var snapshot = new AutomationSnapshot([], Catalog, Pairs, true, "ready", "", DateTimeOffset.UtcNow, [], CompositeDetectionVersion: 1);
        Assert.True(editor.CanSave(snapshot)); Assert.False(editor.CanSave(snapshot with { CompositeDetectionVersion = 0 }));
        Assert.False(editor.CanSave(snapshot with { Capabilities = [State, Other, unsupportedAction] }));
        Assert.False(editor.SupportsAction(unsupportedAction, Pairs)); Assert.Throws<ArgumentException>(() => editor.Select(unsupportedAction, Pairs));
        Assert.Throws<ArgumentException>(() => editor.SelectLeaf([1], Other with { IsStateCondition = false }, Pairs));
        Assert.Throws<ArgumentException>(() => editor.SelectLeaf([1], State with { Id = "synthetic.feedback" }, Pairs));
        Assert.Equal(Other.Id, editor.Node([1]).Invocation!.CapabilityId); Assert.Equal("retained", editor.Action!.Parameters["target"]);
        editor.WrapGroup([0]); editor.WrapGroup([0, 0]); var before = editor.DetectionExpression;
        Assert.Throws<ArgumentException>(() => editor.WrapGroup([0, 0, 0])); Assert.Same(before, editor.DetectionExpression);
        editor.Load(Single()); editor.EnableComposite(Catalog, Pairs);
        for (var i = 2; i < TaskDetectionExpressionLimits.MaximumLeaves; i++) editor.AddLeaf([]);
        before = editor.DetectionExpression; Assert.Throws<ArgumentException>(() => editor.AddLeaf([])); Assert.Same(before, editor.DetectionExpression);
        Assert.Equal("original", editor.Node([0]).Invocation!.Parameters["value"]);
        Assert.False(editor.Complete); Assert.Throws<InvalidOperationException>(() => editor.Build());
    }
}
