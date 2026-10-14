using System.Collections.Immutable;
using Axiom.Domain;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class CapabilityPickerTests
{
    private static readonly TaskCapability State = new("synthetic.state", 1, TaskCapabilityKind.Detection, "程序", "窗口状态", "Known Window State",
        [new("value", "状态", TaskFieldKind.Text, true, "", "default", 32, [])], "", "", IsStateCondition: true);
    private static readonly TaskCapability Other = State with { Id = "synthetic.other", Category = "电源", Name = "供电状态", Description = "Known Power State" };
    private static readonly TaskCapability Event = State with { Id = "synthetic.event", Category = "程序", Name = "程序启动", IsStateCondition = false };
    private static readonly TaskCapability Action = new("synthetic.action", 1, TaskCapabilityKind.Action, "提醒", "显示提醒", "只显示一次",
        [new("target", "内容", TaskFieldKind.Text, true, "", "default", 32, [])], "", "", SupportsCompositeDetection: true);
    private static readonly ImmutableArray<TaskCombination> Pairs = [new(State.Id, Action.Id), new(Other.Id, Action.Id), new(Event.Id, Action.Id)];
    private static TaskInvocation Invocation(TaskCapability capability, string key, string value) => new(capability.Id, capability.SchemaVersion,
        ImmutableDictionary<string, string>.Empty.Add(key, value));
    private static AutomationTask Task() => new(Guid.NewGuid().ToString("D"), 3, "合成规则", true,
        Invocation(State, "value", "retained-state"), Invocation(Action, "target", "retained-action"));
    private static AutomationSnapshot Snapshot(params TaskCapability[] capabilities) => new([], [.. capabilities], Pairs, true, "ready", "", DateTimeOffset.UtcNow, [], CompositeDetectionVersion: 1);

    [Fact]
    public void CategoriesAndSearchComeFromCandidateDeclarationsAndKeepMatchingSelection()
    {
        var picker = new CapabilityPickerState([State, Other, Event], State.Id);
        Assert.Equal(2, picker.Categories.Length); Assert.Contains("程序", picker.Categories); Assert.Contains("电源", picker.Categories);
        picker.Filter("程序", "  wInDoW  ");
        Assert.Same(State, picker.Selected); Assert.Equal(new[] { State, Event }, picker.Visible);
        picker.Filter(null, "窗口"); Assert.Equal(new[] { State }, picker.Visible); Assert.Same(State, picker.Selected);
        picker.Filter(null, "程序"); Assert.Equal(new[] { State, Event }, picker.Visible); Assert.Same(State, picker.Selected);
        picker.Filter(null, ""); Assert.Equal(3, picker.Visible.Length); Assert.Same(State, picker.Selected);
    }

    [Fact]
    public void HiddenSelectionAndEmptyResultsCannotConfirmOrSilentlySelectAnotherType()
    {
        var picker = new CapabilityPickerState([State, Other], State.Id);
        picker.Filter("电源", ""); Assert.Null(picker.Selected); Assert.Equal(new[] { Other }, picker.Visible);
        picker.Select(State); Assert.Null(picker.Selected);
        picker.Select(Other); Assert.Same(Other, picker.Selected);
        picker.Filter(null, "no-match"); Assert.Empty(picker.Visible); Assert.Null(picker.Selected);
        picker.Filter(null, ""); Assert.Null(picker.Selected);
        var empty = new CapabilityPickerState([], "missing"); Assert.Empty(empty.Categories); Assert.Empty(empty.Visible); Assert.Null(empty.Selected);
    }

    [Fact]
    public void CancelingFilteredDraftLeavesUnavailableOriginalAndEveryParameterUntouched()
    {
        var task = Task(); var editor = new TaskEditorState(); editor.Load(task);
        // The saved detection may be missing from the current catalog. Merely opening its picker must not replace it.
        var picker = new CapabilityPickerState([Other, Event], editor.Detection!.CapabilityId);
        Assert.Null(picker.Selected); picker.Select(Other); picker.Filter("程序", ""); picker.Select(Event);
        Assert.Same(task.Detection, editor.Detection); Assert.Same(task.Action, editor.Action); Assert.False(editor.Dirty);
        editor.EnableComposite([State, Other, Action], Pairs); editor.SelectLeaf([1], Other, Pairs);
        editor.SetLeafParameter([1], "value", "leaf-input");
        var tree = editor.DetectionExpression;
        var leafDraft = new CapabilityPickerState([State, Other], Other.Id); leafDraft.Select(State); leafDraft.Filter(null, "no-match");
        Assert.Same(tree, editor.DetectionExpression); Assert.Equal("leaf-input", editor.Node([1]).Invocation!.Parameters["value"]);
    }

    [Fact]
    public void ConfirmingCurrentDetectionActionOrLeafPreservesParametersAndDirtyState()
    {
        var task = Task(); var editor = new TaskEditorState(); editor.Load(task);
        foreach (var capability in new[] { State, Action })
        {
            var picker = new CapabilityPickerState([capability], capability.Id); picker.Filter(capability.Category, capability.Name);
            editor.Select(picker.Selected!, Pairs);
        }
        Assert.Same(task.Detection, editor.Detection); Assert.Same(task.Action, editor.Action); Assert.False(editor.Dirty);
        editor.EnableComposite([State, Other, Action], Pairs); editor.SelectLeaf([1], Other, Pairs); editor.SetLeafParameter([1], "value", "leaf-input");
        editor.Load(editor.Build()); var tree = editor.DetectionExpression; var version = editor.TreeVersion;
        var leafPicker = new CapabilityPickerState([State, Other], Other.Id); editor.SelectLeaf([1], leafPicker.Selected!, Pairs);
        Assert.Same(tree, editor.DetectionExpression); Assert.Equal(version, editor.TreeVersion); Assert.False(editor.Dirty);
        Assert.Equal("leaf-input", editor.Node([1]).Invocation!.Parameters["value"]);
    }

    [Fact]
    public void CandidateListsRespectStateMetadataAndEveryLeafActionCompatibility()
    {
        var editor = new TaskEditorState(); editor.Load(Task());
        var incompatibleState = Other with { Id = "synthetic.feedback" };
        var singleOnlyAction = Action with { Id = "synthetic.single", SupportsCompositeDetection = false };
        var partialAction = Action with { Id = "synthetic.partial" };
        var snapshot = Snapshot(State, Other, Event, incompatibleState, Action, singleOnlyAction, partialAction) with
        { Combinations = [.. Pairs, new(State.Id, singleOnlyAction.Id), new(Other.Id, singleOnlyAction.Id), new(State.Id, partialAction.Id)] };
        Assert.Equal(new[] { State, Other, Event, incompatibleState }, CapabilityPickerState.Candidates(snapshot, editor, TaskCapabilityKind.Detection));
        Assert.Equal(new[] { State, Other }, CapabilityPickerState.Candidates(snapshot, editor, TaskCapabilityKind.Detection, stateOnly: true));
        Assert.Equal(new[] { Action, singleOnlyAction, partialAction }, CapabilityPickerState.Candidates(snapshot, editor, TaskCapabilityKind.Action));
        editor.EnableComposite(snapshot.Capabilities, snapshot.Combinations); editor.SelectLeaf([1], Other, snapshot.Combinations);
        Assert.Equal(new[] { Action }, CapabilityPickerState.Candidates(snapshot, editor, TaskCapabilityKind.Action));
        Assert.Equal(new[] { State, Other }, CapabilityPickerState.Candidates(snapshot, editor, TaskCapabilityKind.Detection, stateOnly: true));
    }

    [Fact]
    public void OnlyConfirmingNewDetectionClearsAnIncompatibleAction()
    {
        var editor = new TaskEditorState(); editor.Load(Task());
        var incompatible = Other with { Id = "synthetic.incompatible" };
        var picker = new CapabilityPickerState([State, incompatible], State.Id); picker.Select(incompatible);
        Assert.Equal("retained-action", editor.Action!.Parameters["target"]);
        editor.Select(picker.Selected!, Pairs);
        Assert.Null(editor.Action); Assert.Equal(incompatible.Id, editor.Detection!.CapabilityId); Assert.Equal("default", editor.Detection.Parameters["value"]);
    }
}
