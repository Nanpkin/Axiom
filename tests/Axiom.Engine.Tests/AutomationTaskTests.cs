using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class AutomationTaskTests
{
    private static readonly ResourceScope Scope = ResourceScope.Session("user", 1);
    private static AutomationTask Hdr() => GameHdrTasks.FromLegacy(new(Guid.NewGuid().ToString("D"), 0, "task", "game.exe", "screen", true, false));

    [Fact]
    public void RegisteredDetectionAndActionCompileIntoExistingEngineRule()
    {
        var rule = GameHdrTasks.CreateCompiler().Compile(Hdr(), "user", Scope);
        Assert.Equal("process.running", rule.Condition.Key!.CapabilityId);
        Assert.Equal("game.exe", rule.Condition.Key.ResourceId);
        Assert.Equal("display.hdr", rule.Action.Key.CapabilityId);
        Assert.Equal(RuleActionKind.Desired, rule.Action.Kind);
        Assert.True(rule.Action.Value.Boolean);
    }
    [Fact]
    public void UnknownSchemaTypeAndExtraParametersAreRejectedBeforeCompilation()
    {
        var compiler = GameHdrTasks.CreateCompiler(); var task = Hdr();
        Assert.Throws<ArgumentException>(() => compiler.Compile(task with { SchemaVersion = 2 }, "user", Scope));
        Assert.Throws<ArgumentException>(() => compiler.Compile(task with { Action = task.Action with { CapabilityId = "shell" } }, "user", Scope));
        Assert.Throws<ArgumentException>(() => compiler.Compile(task with { Action = task.Action with { SchemaVersion = 2 } }, "user", Scope));
        Assert.Throws<ArgumentException>(() => compiler.Compile(task with { Action = task.Action with { Parameters = task.Action.Parameters.Add("command", "anything") } }, "user", Scope));
        Assert.Throws<ArgumentException>(() => compiler.Compile(task with { Action = task.Action with { Parameters = task.Action.Parameters.SetItem("enabled", "yes") } }, "user", Scope));
        Assert.Throws<ArgumentException>(() => compiler.Compile(task with { Detection = task.Detection with { Parameters = task.Detection.Parameters.SetItem("processNames", "../game.exe") } }, "user", Scope));
    }
    [Fact]
    public void LegacyRoundTripPreservesIdentityVersionPauseAndTarget()
    {
        var legacy = new GameHdrRuleDto(Guid.NewGuid().ToString("D"), 12, "existing", "app.exe", "offline-display", false, true);
        var roundTrip = GameHdrTasks.ToLegacy(GameHdrTasks.FromLegacy(legacy));
        Assert.Equal(legacy with { ProcessNamesText = "app.exe" }, roundTrip);
    }
    [Fact]
    public void MultipleProcessesCompileAsAnyAndUnknownDoesNotMeanAllExited()
    {
        var task = Hdr() with { Detection = Hdr().Detection with
            { Parameters = ImmutableDictionary<string, string>.Empty.Add("processNames", "GameA.exe\r\ngameb.exe\nGAMEA.exe") } };
        var condition = GameHdrTasks.CreateCompiler().Compile(task, "user", Scope).Condition;
        Assert.Equal(ConditionKind.Any, condition.Kind);
        Assert.Equal(2, condition.Dependencies.Count);
        var first = new ResourceKey("process.running", "gamea.exe", Scope);
        var second = new ResourceKey("process.running", "gameb.exe", Scope);
        var source = new SourceSession("test", 1);
        ObservedState State(ResourceKey key, bool? running) => new(new(key,
            running.HasValue ? StateQuality.Known : StateQuality.Unknown,
            running.HasValue ? StateValue.From(new("axiom.bool", 1), running.Value) : null, DateTimeOffset.UtcNow), source, 1, 1);
        Assert.Equal(TruthValue.True, RuleEvaluator.Evaluate(condition,
            new Dictionary<ResourceKey, ObservedState> { [first] = State(first, true), [second] = State(second, false) }).Value);
        Assert.Equal(TruthValue.True, RuleEvaluator.Evaluate(condition,
            new Dictionary<ResourceKey, ObservedState> { [first] = State(first, false), [second] = State(second, true) }).Value);
        Assert.Equal(TruthValue.False, RuleEvaluator.Evaluate(condition,
            new Dictionary<ResourceKey, ObservedState> { [first] = State(first, false), [second] = State(second, false) }).Value);
        Assert.Equal(TruthValue.Unknown, RuleEvaluator.Evaluate(condition,
            new Dictionary<ResourceKey, ObservedState> { [first] = State(first, false), [second] = State(second, null) }).Value);
        Assert.Equal(["gamea.exe", "gameb.exe"], ProcessNameSelection.Parse(task.Detection.Parameters["processNames"]));
        Assert.Throws<ArgumentException>(() => ProcessNameSelection.Parse("a.exe\n../b.exe"));
    }
    private sealed class TestDetection : ITaskDetectionCompiler
    {
        public TaskCapability Descriptor => new("test.session", 1, TaskCapabilityKind.Detection, "会话", "会话状态", "测试",
            [new("state", "状态", TaskFieldKind.Choice, true, "", "locked", 16, [new("locked", "锁定"), new("unlocked", "解锁")])], "", "");
        public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Equal(new("test.session", "current", scope), StateValue.From(new("test.state", 1), configuration.Parameters["state"]));
    }
    private sealed class TestAction : ITaskActionCompiler
    {
        public TaskCapability Descriptor => new("test.message", 1, TaskCapabilityKind.Action, "通知", "消息", "测试",
            [new("text", "内容", TaskFieldKind.Text, true, "", null, 100, [])], "执行一次", "");
        public RuleAction Compile(TaskInvocation configuration, ResourceScope scope) => RuleAction.Command(new("test.message", "desktop", scope), StateValue.From(new("test.text", 1), configuration.Parameters["text"]));
    }
    [Fact]
    public void SecondCapabilityPairUsesSameEditorAndCompilerWithoutHdrParameters()
    {
        var compiler = GameHdrTasks.CreateCompiler(); var detection = new TestDetection(); var action = new TestAction();
        compiler.Register(detection); compiler.Register(action);
        var editor = new TaskEditorState(); editor.Load(null); editor.Title = "锁定后通知";
        ImmutableArray<TaskCombination> combinations = [new("test.session", "test.message")];
        editor.Select(detection.Descriptor, combinations); editor.Select(action.Descriptor, combinations);
        editor.SetParameter(TaskCapabilityKind.Action, "text", "完成");
        var task = editor.Build(); var rule = compiler.Compile(task, "user", Scope);
        Assert.Equal("locked", rule.Condition.Expected!.Text);
        Assert.Equal("完成", rule.Action.Value.Text);
        Assert.Equal(RuleActionKind.Command, rule.Action.Kind);
        Assert.DoesNotContain("display", task.Action.Parameters.Keys);
    }
    [Fact]
    public void BooleanWithoutExplicitDefaultMatchesVisibleOffValue()
    {
        var action = new TestAction().Descriptor with { Fields = [new("enabled", "启用", TaskFieldKind.Boolean, true, "", null, 5, [])] };
        var editor = new TaskEditorState();
        editor.Select(action, []);
        Assert.Equal("false", editor.Action!.Parameters["enabled"]);
        AutomationTaskCompiler.ValidateInvocation(editor.Action, action);
    }
    [Fact]
    public void SwitchingDetectionClearsIncompatibleActionAndOldFields()
    {
        var editor = new TaskEditorState(); editor.Load(Hdr());
        editor.Select(new TestDetection().Descriptor, [new("test.session", "test.message")]);
        Assert.Null(editor.Action); Assert.False(editor.Complete); Assert.True(editor.Dirty);
        Assert.False(editor.Detection!.Parameters.ContainsKey("processNames"));
        Assert.Throws<InvalidOperationException>(() => editor.Build());
    }
    [Fact]
    public void EditorPreservesOfflineResourceAndDoesNotMarkUnchangedLoadDirty()
    {
        var editor = new TaskEditorState(); var task = Hdr(); editor.Load(task);
        Assert.False(editor.Dirty);
        editor.SetParameter(TaskCapabilityKind.Action, "enabled", "false"); Assert.True(editor.Dirty);
        editor.SetParameter(TaskCapabilityKind.Action, "enabled", "true"); Assert.False(editor.Dirty);
        Assert.Equal("screen", editor.Build().Action.Parameters["display"]);
    }
    private sealed class Backend(TaskCombination combination) : IAutomationTaskBackend
    {
        public ImmutableArray<TaskCombination> Combinations => [combination];
        public Task<AutomationSnapshot> ReadAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<AutomationTask> SaveAsync(AutomationTask task, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteAsync(string id, long version, CancellationToken token) => throw new NotSupportedException();
    }
    [Fact]
    public void RouterSelectsRegisteredRuntimeAndRejectsUnsupportedPairs()
    {
        var backend = new Backend(new("process.running", "display.hdr")); var router = new AutomationTaskRouter([backend]);
        Assert.Same(backend, router.Resolve(Hdr()));
        Assert.Throws<ArgumentException>(() => new AutomationTaskRouter([backend, backend]));
        var task = Hdr(); Assert.Throws<ArgumentException>(() => router.Resolve(task with { Action = task.Action with { CapabilityId = "other" } }));
    }
    [Fact]
    public async Task ProtocolRoundTripsGenericTaskAndRejectsResponseFieldsInRequest()
    {
        using var stream = new MemoryStream(); var request = new AutomationMessage(1, "save", "request", DateTimeOffset.UtcNow.AddSeconds(10), Task: Hdr());
        await AutomationProtocol.WriteAsync(stream, request, default); stream.Position = 0;
        var read = await AutomationProtocol.ReadAsync(stream, default);
        AutomationProtocol.ValidateRequest(read, DateTimeOffset.UtcNow);
        Assert.Equal("game.exe", read.Task!.Detection.Parameters["processNames"]);
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Error = "injected" }, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Type = "execute-shell" }, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1) }, DateTimeOffset.UtcNow));
        using var oversized = new MemoryStream(BitConverter.GetBytes(AutomationProtocol.MaximumFrame + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => AutomationProtocol.ReadAsync(oversized, default));
    }
}
