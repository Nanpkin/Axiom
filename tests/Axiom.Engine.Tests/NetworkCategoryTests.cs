using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class NetworkCategoryTests
{
    private static TaskInvocation Detection(string category = "public") => new(NetworkCategoryCapabilities.State, 1,
        ImmutableDictionary<string, string>.Empty.Add("category", category));
    [Fact]
    public void ConnectedKnownMatchesWinButUnknownAndIncompleteEnumerationCannotMeanNonMatch()
    {
        var known = new NetworkCategorySnapshot([0, 1, 2]);
        for (var category = 0; category < 3; category++) Assert.True(NetworkCategoryState.Evaluate(known, category));
        var partial = new NetworkCategorySnapshot([1, null, 99]);
        Assert.True(NetworkCategoryState.Evaluate(partial, 1)); Assert.Null(NetworkCategoryState.Evaluate(partial, 0)); Assert.Null(NetworkCategoryState.Evaluate(partial, 2));
        Assert.True(NetworkCategoryState.Evaluate(new([2], false), 2)); Assert.Null(NetworkCategoryState.Evaluate(new([2], false), 0));
        for (var category = 0; category < 3; category++) Assert.False(NetworkCategoryState.Evaluate(new([]), category));
        Assert.False(NetworkCategoryState.Evaluate(new([0, 0]), 1)); Assert.Null(NetworkCategoryState.Evaluate(new([], false), 0));
    }
    [Fact]
    public void BaselinesAndRecoveryArePerCategoryAndUnchangedSnapshotsStaySilent()
    {
        var state = new NetworkCategoryState(); var first = state.Read(new([0]))!;
        Assert.Equal(new bool?[] { true, false, false }, first.Matches); Assert.All(first.Baselines, Assert.True);
        Assert.Null(state.Read(new([0, 0])));
        var partial = state.Read(new([0, null]))!; Assert.True(partial.Matches[0]); Assert.Null(partial.Matches[1]); Assert.All(partial.Baselines, Assert.False);
        var switchAndRecovery = state.Read(new([1]))!;
        Assert.Equal(new bool?[] { false, true, false }, switchAndRecovery.Matches);
        Assert.Equal(new[] { false, true, true }, switchAndRecovery.Baselines); // Public changed from Known; the others recovered from Unknown.
        var stopped = state.Read(new([]))!; Assert.False(stopped.Matches[1]); Assert.False(stopped.Baselines[1]);
    }
    [Fact]
    public void PublicCallbackAbiCoversConnectionAndCategoryChangesAndIgnoresNameAndDescription()
    {
        var callback = typeof(INetworkCategoryCallback); Assert.Equal(new Guid("DCB00004-570F-4A9B-8D69-199FDBA5723B"), callback.GUID);
        Assert.True(callback.IsPublic); Assert.True(callback.GetCustomAttribute<ComVisibleAttribute>()!.Value);
        Assert.Equal(ComInterfaceType.InterfaceIsIUnknown, callback.GetCustomAttribute<InterfaceTypeAttribute>()!.Value);
        Assert.Equal(new[] { "NetworkAdded", "NetworkDeleted", "NetworkConnectivityChanged", "NetworkPropertyChanged" }, callback.GetMethods().Select(m => m.Name));
        Assert.All(callback.GetMethods(), method => Assert.NotEqual(0, (int)(method.GetMethodImplementationFlags() & MethodImplAttributes.PreserveSig)));
        var notifications = new NetworkCategoryNotifications(); var sink = new NetworkCategoryCallbackSink(notifications.Hint); var id = Guid.NewGuid();
        sink.NetworkAdded(id); sink.NetworkDeleted(id); sink.NetworkConnectivityChanged(id, uint.MaxValue);
        sink.NetworkPropertyChanged(id, 0x10); sink.NetworkPropertyChanged(id, 1); sink.NetworkPropertyChanged(id, 2 | 4 | 8);
        Assert.Equal(5, notifications.Version); var count = 0; while (notifications.Events.TryRead(out _)) count++; Assert.Equal(5, count);
    }
    [Fact(Timeout = 10000)]
    public async Task NotificationDuringQueryDiscardsSupersededResultAndOverflowRejectsBufferedKnown()
    {
        var notifications = new NetworkCategoryNotifications(); using var stop = new CancellationTokenSource(); var reads = 0;
        var published = new List<NetworkCategorySample>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => NetworkCategoryObservation.Run(notifications, () =>
        {
            if (++reads == 1) { notifications.Hint(); return new([0]); }
            return new([1]);
        }, sample => { published.Add(sample); stop.Cancel(); }, stop.Token)));
        Assert.Equal(2, reads); var only = Assert.Single(published); Assert.False(only.Matches[0]); Assert.True(only.Matches[1]); Assert.True(only.Baselines[1]);
        var lost = new NetworkCategoryNotifications(); for (var i = 0; i < 65; i++) lost.Hint(); Assert.True(lost.Failed);
        Assert.Throws<IOException>(() => NetworkCategoryObservation.Run(lost, () => throw new InvalidOperationException("No query after failure"), _ => throw new InvalidOperationException("No stale publish"), default));
    }
    [Fact(Timeout = 10000)]
    public async Task ProviderBaselinesDoNotReplayCommandsButInitialKnownCanHoldDesiredAndFailureDropsCache()
    {
        var executor = new Executor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(NetworkCategoryCapabilities.State, schema: schema));
        var condition = new NetworkCategoryTaskDetection().Compile(Detection(), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var engineSource = fixture.Engine.RegisterSource("synthetic-category", [key]); long sequence = 0;
        var output = new ResourceKey("output", "category-command", ResourceScope.Machine); var fake = new Source(); using var stop = new CancellationTokenSource();
        var updates = Channel.CreateBounded<ProviderUpdate>(16);
        var run = new NetworkCategoryCapabilityProvider(() => fake).ObserveAsync([new(NetworkCategoryCapabilities.State, "public")], update =>
        {
            var sample = Assert.Single(update.Samples); var observed = new Observation(key, sample.Quality,
                sample.Boolean.HasValue ? StateValue.From(schema, sample.Boolean.Value) : null, fixture.Clock.UtcNow);
            if (update.Snapshot) fixture.Engine.ApplySnapshot(engineSource, ++sequence, [observed]); else fixture.Engine.Update(engineSource, ++sequence, observed);
            Assert.True(updates.Writer.TryWrite(update));
        }, stop.Token);
        await updates.Reader.ReadAsync(); fake.Emit(new NetworkCategoryState().Read(new([0]))!); await updates.Reader.ReadAsync();
        fixture.Engine.SetRule(new("awake", 1, "owner", ResourceScope.Machine, condition, RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true))));
        fixture.Engine.SetRule(new("command", 1, "owner", ResourceScope.Machine, condition, RuleAction.Command(output, StateValue.From(AutomationFixture.Schema, true))));
        fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized); Assert.Equal(0, executor.Commands);
        fixture.Update(true, AutomationFixture.Output);
        var state = new NetworkCategoryState(); state.Read(new([0]));
        fake.Emit(state.Read(new([1]))!); await updates.Reader.ReadAsync(); Assert.False(executor.Authorized);
        fake.Emit(state.Read(new([0]))!); await updates.Reader.ReadAsync(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Commands);
        fake.Emit(state.Read(new([], false))!); await updates.Reader.ReadAsync(); Assert.False(executor.Authorized);
        fake.Emit(state.Read(new([0]))!); Assert.True((await updates.Reader.ReadAsync()).Snapshot); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Commands);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.Equal(1, fake.Disposals);
        var lost = new Source(); lost.Emit(new([true, false, false], [false, false, false])); lost.Fail();
        using var stopped = new CancellationTokenSource(); var isolated = Channel.CreateBounded<ProviderUpdate>(4);
        var failedRun = new NetworkCategoryCapabilityProvider(() => lost).ObserveAsync([new(NetworkCategoryCapabilities.State, "public")], u => Assert.True(isolated.Writer.TryWrite(u)), stopped.Token);
        await isolated.Reader.ReadAsync(); Assert.Equal(StateQuality.Unknown, Assert.Single((await isolated.Reader.ReadAsync()).Samples).Quality);
        Assert.False(isolated.Reader.TryRead(out _)); await lost.Disposed.Task; Assert.Equal(1, lost.Disposals);
        stopped.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failedRun);
    }
    [Fact]
    public async Task RealRegistryTemplateAndStrictImportPersistOnlyTheCategoryAndPausedConfiguration()
    {
        var plugins = BuiltInAutomationPlugins.Create(); var compiler = new AutomationTaskCompiler();
        foreach (var p in plugins) { foreach (var d in p.Detections) compiler.Register(d); foreach (var a in p.Actions) compiler.Register(a); }
        var catalog = new AutomationSnapshot([], compiler.Catalog, [.. plugins.SelectMany(p => p.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "public-network-reminder"); Assert.True(template.Draft.Paused); Assert.Equal(0, template.Draft.Version);
        Assert.NotNull(compiler.Compile(template.Draft, "owner", ResourceScope.Session("owner", 1)));
        Assert.Contains("不证明安全可信", NetworkCategoryCapabilities.Descriptor.Fields[0].Hint);
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "synthetic category", "synthetic proposal", template.Draft.Detection,
            template.Draft.Action with { Parameters = template.Draft.Action.Parameters.SetItem("title", "").SetItem("body", "") });
        var imported = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(imported.Paused); Assert.Equal("", imported.Action.Parameters["body"]);
        Assert.Throws<ArgumentException>(() => compiler.Compile(imported, "owner", ResourceScope.Session("owner", 1)));
        imported = imported with { Action = template.Draft.Action }; // User fills the reviewed local notification text before saving.
        foreach (var invalid in new[] { "PUBLIC", "trusted", "", "3" })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection(invalid) }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => NetworkCategoryCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.Add("networkId", Guid.NewGuid().ToString()) }));
        var path = Path.Combine(Path.GetTempPath(), "axiom-category-" + Guid.NewGuid().ToString("N") + ".db");
        try { using var store = new SqliteAutomationTaskStore(path); await store.SaveAsync(imported, default); var saved = Assert.Single(await store.ListAsync()); Assert.True(saved.Paused); Assert.Equal("public", Assert.Single(saved.Detection.Parameters).Value); }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    private sealed class Source : INetworkCategorySource
    {
        private readonly Channel<NetworkCategorySample> queue = Channel.CreateBounded<NetworkCategorySample>(64);
        public ChannelReader<NetworkCategorySample> Events => queue.Reader;
        public bool Failed { get; private set; }
        internal int Disposals;
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Emit(NetworkCategorySample sample) => Assert.True(queue.Writer.TryWrite(sample));
        internal void Fail() { Failed = true; queue.Writer.TryComplete(new IOException("synthetic notification failed")); }
        public void Dispose() { Disposals++; queue.Writer.TryComplete(); Disposed.TrySetResult(); }
    }
    private sealed class Executor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        internal bool Authorized; internal int Commands;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) { if (key == AutomationFixture.Output) Authorized = authorized; }
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic lease", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) { Commands++; return ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic command", command.CommandId)); }
    }
}
