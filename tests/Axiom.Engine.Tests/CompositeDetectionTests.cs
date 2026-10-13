using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;
using Microsoft.Data.Sqlite;

namespace Axiom.Engine.Tests;

public sealed class CompositeDetectionTests
{
    private static readonly ResourceScope Scope = ResourceScope.Machine;
    private static readonly ValueSchema Bool = new("axiom.bool", 1);
    private static TaskInvocation Invocation(string id, params (string Key, string Value)[] parameters) => new(id, id == "process.running" ? 2 : 1,
        parameters.ToImmutableDictionary(p => p.Key, p => p.Value));
    private static TaskInvocation Process(string names = "a.exe") => Invocation("process.running", ("processNames", names));
    private static TaskDetectionExpression Leaf(TaskInvocation invocation) => new("leaf", invocation);
    private static TaskDetectionExpression Group(string kind, params TaskDetectionExpression[] children) => new(kind, Children: [.. children]);
    private static TaskDetectionExpression Example() => Group("all", Group("any", Leaf(Process()), Leaf(Process("b.exe"))),
        Leaf(Invocation(PowerStateCapabilities.State, ("mode", "ac"), ("threshold", "25"))), Group("not", Leaf(Invocation(SessionLockStateCapabilities.State, ("state", "locked")))));
    private static TaskInvocation Awake() => Invocation(KeepAwakeCapabilities.Hold, ("target", "display"));
    private static AutomationTask TaskFor(TaskDetectionExpression? tree = null, TaskInvocation? action = null) => new(Guid.NewGuid().ToString("D"), 0, "synthetic composite", false,
        tree is null ? Process() : TaskDetectionExpressions.Validate(tree)[0], action ?? Awake(), tree is null ? 1 : 3, DetectionExpression: tree);
    private static (AutomationTaskCompiler Compiler, AutomationSnapshot Catalog) Registered()
    {
        var modules = BuiltInAutomationPlugins.Create(); var compiler = new AutomationTaskCompiler(supportsCompositeDetection: true);
        foreach (var module in modules)
        {
            foreach (var d in module.Detections) compiler.Register(d);
            foreach (var a in module.Actions) compiler.Register(a);
            foreach (var combination in module.Combinations) compiler.RegisterCombination(combination);
        }
        return (compiler, new([], compiler.Catalog, [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UnixEpoch, [], CompositeDetectionVersion: 1));
    }

    [Fact]
    public void BoundedTreeAndVersionAnchorChecksCannotSilentlyDropConditions()
    {
        var tree = Example(); var task = TaskFor(tree); Assert.Equal(4, TaskDetectionExpressions.ValidateTask(task).Length);
        Assert.Single(TaskDetectionExpressions.Validate(Group("not", Group("not", Group("not", Leaf(Process())))))); // depth exactly 4
        Assert.Equal(16, TaskDetectionExpressions.Validate(Group("all", [.. Enumerable.Range(0, 16).Select(_ => Leaf(Process()))])).Length);
        foreach (var invalid in new[] { Group("all"), Group("any", Leaf(Process())), Group("not", Leaf(Process()), Leaf(Process())),
            Group("not", Group("not", Group("not", Group("not", Leaf(Process()))))), new TaskDetectionExpression("script", Process()),
            new TaskDetectionExpression("leaf", Process(), [Leaf(Process())]), Group("all", [.. Enumerable.Range(0, 17).Select(_ => Leaf(Process()))]) })
            Assert.Throws<ArgumentException>(() => TaskDetectionExpressions.Validate(invalid));
        Assert.Throws<ArgumentException>(() => TaskDetectionExpressions.ValidateTask(task with { SchemaVersion = 1 }));
        Assert.Throws<ArgumentException>(() => TaskDetectionExpressions.ValidateTask(task with { Detection = Process("other.exe") }));
        Assert.Throws<ArgumentException>(() => TaskDetectionExpressions.ValidateTask(TaskFor() with { SchemaVersion = 3 }));
        var json = JsonSerializer.Serialize(task); var restored = JsonSerializer.Deserialize<AutomationTask>(json)!;
        Assert.Equal(4, TaskDetectionExpressions.ValidateTask(restored).Length);
        Assert.DoesNotContain("DetectionExpression", JsonSerializer.Serialize(TaskFor()));
        Assert.DoesNotContain("CompositeDetectionVersion", JsonSerializer.Serialize(Registered().Catalog with { CompositeDetectionVersion = 0 }));
    }

    [Fact]
    public void NestedAndOrNotRetainsThreeValuedTruthAndAllActualDependencies()
    {
        var condition = Registered().Compiler.Compile(TaskFor(Example()), "owner", Scope).Condition; Assert.Equal(4, condition.Dependencies.Count);
        TruthValue Read(bool? a, bool? b, bool? ac, bool? locked)
        {
            var values = new Dictionary<string, bool?> { ["a.exe"] = a, ["b.exe"] = b, ["ac"] = ac, ["current-session"] = locked };
            var source = new SourceSession("synthetic", 1);
            var states = condition.Dependencies.ToDictionary(k => k, k => new ObservedState(new(k, values[k.ResourceId].HasValue ? StateQuality.Known : StateQuality.Unknown,
                values[k.ResourceId].HasValue ? StateValue.From(Bool, values[k.ResourceId]!.Value) : null, DateTimeOffset.UnixEpoch), source, 1, 1));
            return RuleEvaluator.Evaluate(condition, states).Value;
        }
        Assert.Equal(TruthValue.True, Read(true, null, true, false)); // true OR Unknown is true
        Assert.Equal(TruthValue.Unknown, Read(false, null, true, false));
        Assert.Equal(TruthValue.Unknown, Read(true, false, true, null)); // NOT Unknown remains Unknown
        Assert.Equal(TruthValue.False, Read(null, null, false, null)); // false AND Unknown is false
        Assert.Equal(TruthValue.False, Read(true, true, true, true));
        Assert.Equal(TruthValue.True, Read(false, true, true, false));
    }

    [Fact]
    public void EveryLeafMustSupportTheSameActionAndEventsOrLegacyBackendsAreRejected()
    {
        var (compiler, catalog) = Registered(); Assert.All(catalog.Capabilities.Where(c => c.Kind == TaskCapabilityKind.Action), c => Assert.True(c.SupportsCompositeDetection));
        Assert.False(catalog.Capabilities.Single(c => c.Id == SystemEventCapabilities.Event).IsStateCondition);
        Assert.False(catalog.Capabilities.Single(c => c.Id == AxiomStartupPlugin.CapabilityId).IsStateCondition);
        Assert.Throws<ArgumentException>(() => compiler.Compile(TaskFor(Group("not", Leaf(Invocation(SystemEventCapabilities.Event, ("event", "locked"))))), "owner", Scope));
        var feedback = Group("all", Leaf(Process()), Leaf(Invocation(PowerSchemeStateCapabilities.State, ("scheme", "balanced"))));
        Assert.Throws<ArgumentException>(() => compiler.Compile(TaskFor(feedback, Invocation(PowerSchemeCapabilities.Set, ("scheme", "balanced"), ("delaySeconds", "0"))), "owner", Scope));
        var hdr = GameHdrTasks.FromLegacy(new(Guid.NewGuid().ToString("D"), 0, "old", "a.exe", "synthetic-display", true, false));
        Assert.False(GameHdrTasks.CreateCompiler().Catalog.Single(c => c.Kind == TaskCapabilityKind.Action).SupportsCompositeDetection);
        var hdrTree = hdr with { SchemaVersion = 3, DetectionExpression = Group("all", Leaf(hdr.Detection), Leaf(hdr.Detection)) };
        Assert.Throws<ArgumentException>(() => GameHdrTasks.CreateCompiler().Compile(hdrTree, "owner", Scope));
        Assert.Throws<ArgumentException>(() => GameHdrTasks.ToLegacy(hdrTree));
        var task = TaskFor(Example()); var leaves = TaskDetectionExpressions.Validate(task.DetectionExpression!);
        var complete = new Backend(true, [.. leaves.Select(l => new TaskCombination(l.CapabilityId, task.Action.CapabilityId)).Distinct()]);
        Assert.Same(complete, new AutomationTaskRouter([complete]).Resolve(task));
        Assert.Throws<ArgumentException>(() => new AutomationTaskRouter([new Backend(false, complete.Combinations)]).Resolve(task));
        Assert.Throws<ArgumentException>(() => new AutomationTaskRouter([new Backend(true, [complete.Combinations[0]])]).Resolve(task));
        var eventCompiler = new AutomationTaskCompiler(true); eventCompiler.Register(new MisdeclaredEvent()); eventCompiler.Register(new KeepAwakeTaskAction());
        eventCompiler.RegisterCombination(new("synthetic.event", KeepAwakeCapabilities.Hold));
        Assert.Throws<ArgumentException>(() => eventCompiler.Compile(TaskFor(Leaf(Invocation("synthetic.event"))), "owner", Scope));
    }

    [Fact]
    public async Task SqliteAndPipeRoundTripsKeepOldTasksAndProtectCompositeDataFromOldApplications()
    {
        var (compiler, _) = Registered(); var audio = Invocation(AudioPlaybackCapabilities.Set, ("operation", "mute"), ("percent", "30"), ("delaySeconds", "0"));
        var old = TaskFor(action: audio); var timed = TaskFor(action: audio) with { SchemaVersion = 2, TimeWindow = new(540, 1080, 62) };
        var composed = TaskFor(Example(), audio) with { TimeWindow = timed.TimeWindow };
        var path = Path.Combine(Path.GetTempPath(), "axiom-synthetic-composite-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path))
            {
                await store.SaveAsync(old, default); await store.SaveAsync(timed, default); await store.SaveAsync(composed, default);
            }
            using (var reopened = new SqliteAutomationTaskStore(path))
            {
                var tasks = await reopened.ListAsync(); Assert.Equal(3, tasks.Length);
                foreach (var task in tasks) Assert.NotNull(compiler.Compile(task, "owner", Scope));
                var restored = tasks.Single(t => t.Id == composed.Id); Assert.Equal(4, TaskDetectionExpressions.ValidateTask(restored).Length); Assert.Equal(composed.TimeWindow, restored.TimeWindow);
            }
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); using var query = db.CreateCommand(); query.CommandText = "PRAGMA user_version;";
            Assert.Equal(3L, Convert.ToInt64(query.ExecuteScalar())); // old applications reject versions >2 before reading or overwriting tasks
            using var wire = new MemoryStream(); var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            await AutomationProtocol.WriteAsync(wire, new(1, "save", "synthetic", deadline, Task: composed), default); wire.Position = 0;
            var message = await AutomationProtocol.ReadAsync(wire, default); AutomationProtocol.ValidateRequest(message, DateTimeOffset.UtcNow);
            Assert.Equal(4, TaskDetectionExpressions.ValidateTask(message.Task!).Length);
            Assert.Throws<ArgumentException>(() => AutomationProtocol.ValidateRequest(message with { Task = composed with { SchemaVersion = 1 } }, DateTimeOffset.UtcNow));
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact]
    public async Task StrictCompositeImportResolvesEveryAliasAndChecksCapabilityNegotiationAndAllLeafPairs()
    {
        var (compiler, catalog) = Registered(); var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        recorder.Add(new(DateTimeOffset.UtcNow, "foreground", Path.Combine(Path.GetTempPath(), "SyntheticA.exe")));
        recorder.Add(new(DateTimeOffset.UtcNow, "foreground", Path.Combine(Path.GetTempPath(), "SyntheticB.exe")));
        var first = Process(recorder.Session.Objects[0].Id); var second = Process(recorder.Session.Objects[1].Id);
        var tree = Group("all", Group("any", Leaf(first), Leaf(second)), Leaf(Invocation(PowerStateCapabilities.State, ("mode", "ac"), ("threshold", "25"))),
            Group("not", Leaf(Invocation(SessionLockStateCapabilities.State, ("state", "locked")))));
        var proposal = new UsageImport(2, recorder.Session.Id, "existing", "synthetic", "bounded", first, Awake(), tree);
        var result = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(result.Paused); Assert.Equal(3, result.SchemaVersion); Assert.NotNull(compiler.Compile(result, "owner", Scope));
        Assert.All(TaskDetectionExpressions.Validate(result.DetectionExpression!).Where(l => l.CapabilityId == "process.running"), l => Assert.EndsWith(".exe", l.Parameters["processNames"]));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Version = 1 }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog with { CompositeDetectionVersion = 0 }));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog with { Combinations = [.. catalog.Combinations.Where(c => c.DetectionId != SessionLockStateCapabilities.State)] }));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { DetectionExpression = tree with { Children = [Leaf(first), Leaf(Process("app-missing"))] } }), recorder.Session, catalog));
        var exported = UsageAnalysis.CapabilityCatalog(catalog, false); Assert.Contains("IsStateCondition", exported); Assert.Contains("SupportsCompositeDetection", exported); Assert.Contains("compositeDetectionVersion", exported);
        var single = proposal with { Version = 1, DetectionExpression = null };
        Assert.Equal(1, UsageAnalysis.Import(JsonSerializer.Serialize(single), recorder.Session, catalog).Draft!.SchemaVersion);
        var restore = Invocation(ConditionRestoreCapabilities.Playback, ("operation", "volume"), ("percent", "30"), ("delaySeconds", "0"));
        var restoredDraft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = restore }), recorder.Session, catalog).Draft!;
        Assert.Equal(RuleActionKind.Desired, compiler.Compile(restoredDraft, "owner", Scope).Action.Kind);
        var database = Path.Combine(Path.GetTempPath(), "axiom-synthetic-composite-restore-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(database)) await store.SaveAsync(restoredDraft, default);
            using var reopened = new SqliteAutomationTaskStore(database);
            var saved = Assert.Single(await reopened.ListAsync());
            Assert.True(saved.Paused); Assert.Equal(3, saved.SchemaVersion); Assert.Equal(4, TaskDetectionExpressions.ValidateTask(saved).Length);
            Assert.Equal(restoredDraft.Action.Parameters, saved.Action.Parameters);
            Assert.Equal(RuleActionKind.Desired, compiler.Compile(saved, "owner", Scope).Action.Kind);
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(database)!, Path.GetFileName(database) + "*")) File.Delete(file); }
        var restoreIds = new[] { ConditionRestoreCapabilities.Playback, ConditionRestoreCapabilities.Microphone, ConditionRestoreCapabilities.PowerScheme,
            ConditionRestoreCapabilities.ProgramPriority, ConditionRestoreCapabilities.WindowState, BuiltInBrightnessRestoreCapabilities.Set,
            HardwareBrightnessRestoreCapabilities.Set, TaskbarAutoHideRestoreCapabilities.Restore };
        Assert.All(restoreIds, id => Assert.Contains(new TaskCombination(SessionLockStateCapabilities.State, id), catalog.Combinations));
        Assert.DoesNotContain(new TaskCombination(ForegroundScreenCoverCapabilities.Covers, TaskbarAutoHideRestoreCapabilities.Restore), catalog.Combinations);
    }

    [Fact(Timeout = 10000)]
    public async Task CompositeDependenciesDriveCommandEdgesAndDesiredReleasesOnFalseUnknownPauseAndSourceLoss()
    {
        var condition = Registered().Compiler.Compile(TaskFor(Example()), "owner", Scope).Condition;
        var desired = new LeaseExecutor(); using var fixture = new AutomationFixture(desired);
        foreach (var id in condition.Dependencies.Select(k => k.CapabilityId).Distinct()) fixture.Engine.RegisterCapability(AutomationFixture.Binding(id, schema: Bool));
        var source = fixture.Engine.RegisterSource("synthetic-composite", condition.Dependencies); long seq = 0;
        var values = condition.Dependencies.ToDictionary(k => k, k => (bool?)(k.ResourceId != "current-session"));
        void Set(string resource, bool? value, bool baseline = false)
        {
            var key = condition.Dependencies.Single(k => k.ResourceId == resource); values[key] = value;
            var sample = new Observation(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown, value.HasValue ? StateValue.From(Bool, value.Value) : null, fixture.Clock.UtcNow);
            if (baseline) fixture.Engine.ApplySnapshot(source, ++seq, [.. values.Select(v => new Observation(v.Key, v.Value.HasValue ? StateQuality.Known : StateQuality.Unknown, v.Value.HasValue ? StateValue.From(Bool, v.Value.Value) : null, fixture.Clock.UtcNow))]);
            else fixture.Engine.Update(source, ++seq, sample);
        }
        Set("ac", true, true);
        var rule = new RuleDefinition("synthetic-composite-desired", 1, "owner", Scope, condition, RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        fixture.Engine.SetRule(rule); Assert.Equal(4, fixture.Engine.GetObservationPlan().References.Keys.Count(condition.Dependencies.Contains));
        fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.True(desired.Authorized);
        Set("ac", false); Assert.False(desired.Authorized); Set("ac", true); await fixture.Engine.WaitForIdleAsync(); Assert.True(desired.Authorized);
        Set("current-session", null); Assert.False(desired.Authorized); Set("current-session", false, true); await fixture.Engine.WaitForIdleAsync(); Assert.True(desired.Authorized);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", Scope, condition, rule.Action, enabled: false)); Assert.False(desired.Authorized);
        fixture.Engine.SetRule(new(rule.Id, 3, "owner", Scope, condition, rule.Action)); await fixture.Engine.WaitForIdleAsync(); fixture.Engine.ReleaseSource(source); Assert.False(desired.Authorized);
        var command = new ControlledExecutor(); using var cf = new AutomationFixture(command);
        foreach (var id in condition.Dependencies.Select(k => k.CapabilityId).Distinct()) cf.Engine.RegisterCapability(AutomationFixture.Binding(id, schema: Bool));
        var cs = cf.Engine.RegisterSource("synthetic-composite-command", condition.Dependencies); long n = 0;
        Observation O(ResourceKey k, bool? value) => new(k, value.HasValue ? StateQuality.Known : StateQuality.Unknown, value.HasValue ? StateValue.From(Bool, value.Value) : null, cf.Clock.UtcNow);
        var ac = condition.Dependencies.Single(k => k.ResourceId == "ac"); var locked = condition.Dependencies.Single(k => k.ResourceId == "current-session");
        cf.Engine.ApplySnapshot(cs, n++, [.. condition.Dependencies.Select(k => O(k, k != locked))]);
        cf.Engine.SetRule(new("synthetic-composite-command", 1, "owner", Scope, condition, RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true))));
        cf.Engine.StartExecution(); await cf.Engine.WaitForIdleAsync(); Assert.Equal(0, command.Count);
        cf.Engine.Update(cs, n++, O(ac, false)); cf.Engine.Update(cs, n++, O(ac, true)); (await command.NextAsync()).Complete(); await cf.Engine.WaitForIdleAsync(); Assert.Equal(1, command.Count);
        cf.Engine.Update(cs, n++, O(locked, null)); cf.Engine.ApplySnapshot(cs, n++, [.. condition.Dependencies.Select(k => O(k, k != locked))]);
        await cf.Engine.WaitForIdleAsync(); Assert.Equal(1, command.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task LockStateCannotInferUnlockedFromUnknownAndDisconnectResumePreserveBaseline()
    {
        using var stop = new CancellationTokenSource(); using var source = new LockSource(); var updates = Channel.CreateUnbounded<ProviderUpdate>();
        var run = new SessionLockStateCapabilityProvider(() => source).ObserveAsync([new(SessionLockStateCapabilities.State, SessionLockStateCapabilities.Resource)], u => updates.Writer.TryWrite(u), stop.Token);
        Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync()).Samples[0].Quality); Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync()).Samples[0].Quality);
        source.Value = false; source.Emit("session-lock-changed"); var first = await updates.Reader.ReadAsync(); Assert.True(first.Snapshot); Assert.False(first.Samples[0].Boolean);
        source.Value = true; source.Emit("session-lock-changed"); var locked = await updates.Reader.ReadAsync(); Assert.False(locked.Snapshot); Assert.True(locked.Samples[0].Boolean);
        source.Emit("session-lock-reset"); Assert.Equal(StateQuality.Unknown, (await updates.Reader.ReadAsync()).Samples[0].Quality);
        source.Value = false; source.Emit("session-lock-resumed"); var recovery = await updates.Reader.ReadAsync(); Assert.True(recovery.Snapshot); Assert.False(recovery.Samples[0].Boolean);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        using var failed = new LockSource { Failed = true, Value = false }; for (var i = 0; i < 64; i++) failed.Emit("session-lock-changed");
        await Assert.ThrowsAsync<IOException>(() => SessionLockStateObservation.RunAsync(failed, _ => throw new InvalidOperationException("stale Known"), default)); Assert.Equal(0, failed.Reads);
        Assert.DoesNotContain(new SessionLockStatePlugin().Combinations, c => c.ActionId == SessionLockCapabilities.Lock);
    }
    private sealed class Backend(bool supports, ImmutableArray<TaskCombination> pairs) : IAutomationTaskBackend
    {
        public ImmutableArray<TaskCombination> Combinations => pairs;
        public bool SupportsCompositeDetection => supports;
        public Task<AutomationSnapshot> ReadAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<AutomationTask> SaveAsync(AutomationTask task, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteAsync(string id, long expectedVersion, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class MisdeclaredEvent : ITaskDetectionCompiler
    {
        public TaskCapability Descriptor => new("synthetic.event", 1, TaskCapabilityKind.Detection, "", "synthetic", "", [], "", "", IsStateCondition: true);
        public RuleCondition Compile(TaskInvocation invocation, ResourceScope scope) => RuleCondition.Event(new("synthetic.event", "counter", scope), new("axiom.event-count", 1));
    }
    private sealed class LeaseExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public bool Authorized;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) => Authorized = authorized;
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class LockSource : ISessionLockStateSource
    {
        public readonly Channel<string> Queue = Channel.CreateBounded<string>(64);
        public ChannelReader<string> Events => Queue.Reader;
        public bool Failed { get; set; }
        public bool? Value;
        public int Reads;
        public bool? Read(CancellationToken token) { token.ThrowIfCancellationRequested(); Reads++; return Value; }
        public void Emit(string kind) => Assert.True(Queue.Writer.TryWrite(kind));
        public void Dispose() => Queue.Writer.TryComplete();
    }
}
