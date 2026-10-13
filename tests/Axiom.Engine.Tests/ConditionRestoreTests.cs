using System.Collections.Immutable;
using System.IO.Pipes;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;
using static Axiom.Engine.Tests.AutomationFixture;

namespace Axiom.Engine.Tests;

public sealed class ConditionRestoreTests
{
    private static TaskInvocation Invocation(int value = 30) => new(ConditionRestoreCapabilities.Playback, 1,
        ImmutableDictionary<string, string>.Empty.Add("operation", "volume").Add("percent", value.ToString()).Add("delaySeconds", "0"));
    private static readonly ResourceKey Setting = new(ConditionRestoreCapabilities.Playback, "default-playback-volume", ResourceScope.Machine);
    private static CapabilityBinding SyntheticBinding() => new(new(Setting.CapabilityId, RestorableSettingInvocations.Schema, RestorableSettingInvocations.Schema,
        ScopeKind.Machine, true, false, true, ExecutionLocation.Service, "synthetic", true, "ready", true, TimeSpan.FromSeconds(15),
        true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active condition", "owner", []), ScalarKind.Text, ScalarKind.Text);
    private static RuleAction Action(int value) => new RestorableSettingTaskAction(ConditionRestoreCapabilities.PlaybackDescriptor,
        ConditionRestoreCapabilities.Validate, ConditionRestoreCapabilities.ResourceId).Compile(Invocation(value), ResourceScope.Machine);

    [Fact]
    public void WinnerUpdatesKeepFirstBaselineAndReleaseOnlyConfirmedValue()
    {
        var value = 75; var writes = new List<int>();
        var transaction = new ConditionalSettingTransaction<int>(value, () => value, target => { value = target; writes.Add(target); }, "合成设置");
        Assert.True(transaction.Apply(20, default).Owned); Assert.True(transaction.Apply(50, default).Success);
        Assert.True(transaction.Apply(20, default).Success); Assert.True(transaction.Release(default).Success);
        Assert.Equal(75, value); Assert.Equal(new[] { 20, 50, 20, 75 }, writes);
        Assert.True(transaction.Release(default).Success); Assert.Equal(4, writes.Count);
        Assert.False(transaction.Apply(10, default).Success);
    }
    [Fact]
    public void ExternalChangeAndUnknownIdentityRelinquishWithoutOverwritingOrRecapturing()
    {
        var value = 75; var available = true; var writes = 0;
        int Read() => available ? value : throw new IOException("synthetic missing identity");
        var changed = new ConditionalSettingTransaction<int>(value, Read, target => { writes++; value = target; }, "合成设置");
        Assert.True(changed.Apply(20, default).Success); value = 40;
        Assert.False(changed.Apply(50, default).Owned); Assert.True(changed.Release(default).Success);
        Assert.Equal(40, value); Assert.Equal(1, writes);
        var missing = new ConditionalSettingTransaction<int>(value, Read, target => { writes++; value = target; }, "合成设置");
        Assert.True(missing.Apply(25, default).Success); available = false;
        Assert.False(missing.Release(default).Success); available = true;
        Assert.False(missing.Apply(30, default).Success); Assert.Equal(25, value); Assert.Equal(2, writes);
    }
    [Fact]
    public void UnconfirmedOrCanceledWritesNeverClaimRestoreAndPreserveIndependentBaselines()
    {
        var volume = 75; var muted = false; var unknown = false;
        var level = new ConditionalSettingTransaction<int>(volume, () => unknown ? throw new IOException("query failed") : volume,
            value => { volume = value; unknown = true; }, "音量");
        var mute = new ConditionalSettingTransaction<bool>(muted, () => muted, value => muted = value, "静音");
        Assert.False(level.Apply(20, default).Success); Assert.False(level.Owned);
        Assert.True(mute.Apply(true, default).Success); Assert.True(mute.Release(default).Success);
        unknown = false; Assert.True(level.Release(default).Success); Assert.Equal(20, volume); Assert.False(muted);
        using var canceled = new CancellationTokenSource(); var value = 75; var writes = 0;
        var duringWrite = new ConditionalSettingTransaction<int>(value, () => value, target => { value = target; writes++; canceled.Cancel(); }, "合成设置");
        Assert.False(duringWrite.Apply(30, canceled.Token).Success);
        Assert.True(duringWrite.Release(default).Success); Assert.Equal(30, value); Assert.Equal(1, writes);
    }
    [Fact(Timeout = 10000)]
    public async Task CancellationRetainsNativeAdmissionAndHandlesUntilRealWorkerReturn()
    {
        using var canceled = new CancellationTokenSource(); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new BoundedNativeCommand("合成恢复", TimeSpan.FromSeconds(5));
        var native = new BlockingSession(commands, entered, release);
        var apply = native.ApplyAsync(Invocation().Parameters, canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); canceled.Cancel();
        Assert.False((await apply).Success);
        var disposal = native.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted); Assert.False(commands.DrainAsync().IsCompleted);
        Assert.False((await commands.RunAsync(_ => throw new InvalidOperationException("must not overlap"), default)).Success);
        try { Assert.Equal(0, native.Disposed); }
        finally { release.Set(); await disposal.WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.Equal(1, native.Disposed); Assert.True(native.Relinquished);
    }
    [Fact(Timeout = 10000)]
    public async Task ProviderCaptureFailureAndCanceledAcquisitionNeverPublishOwnedTarget()
    {
        using var canceled = new CancellationTokenSource(); var native = new SyntheticSession(75); var captures = 0;
        var provider = Provider((_, _) => { captures++; canceled.Cancel(); return Task.FromResult<IRestorableSettingSession>(native); });
        using var stop = new CancellationTokenSource(); var samples = new List<ProviderSample>();
        var observation = provider.ObserveAsync([new(Setting.CapabilityId, Setting.ResourceId)], update => samples.AddRange(update.Samples), stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.AcquireAsync(Invocation(), canceled.Token));
        Assert.Equal(1, captures); Assert.Equal(1, native.Disposed); Assert.All(samples, sample => Assert.Equal("", sample.Text));
        var failing = Provider((_, _) => throw new IOException("Unknown baseline"));
        await Assert.ThrowsAsync<IOException>(() => failing.AcquireAsync(Invocation(), default));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation);
    }
    [Fact(Timeout = 10000)]
    public async Task RealLeaseStreamUpdatesOneSessionAndReportsReleaseOrDisconnectCleanup()
    {
        foreach (var disconnect in new[] { false, true })
        {
            var native = new SyntheticSession(75); var captures = 0;
            var provider = Provider((_, _) => { captures++; return Task.FromResult<IRestorableSettingSession>(native); });
            var name = "axiom-synthetic-restore-" + Guid.NewGuid().ToString("N");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var connected = server.WaitForConnectionAsync(stop.Token); await client.ConnectAsync(stop.Token); await connected;
            var request = new ProviderMessage(1, "lease-setting", "synthetic-setting", DateTimeOffset.UtcNow.AddSeconds(2), Invocation: Invocation(20));
            ProviderProtocol.Validate(request, DateTimeOffset.UtcNow);
            var work = SessionRestorableLeaseStream.RunAsync(server, request, provider, stop.Token);
            Assert.Equal(OutcomeStatus.Succeeded, (await ProviderProtocol.ReadAsync(client, stop.Token)).Status);
            await ProviderProtocol.WriteAsync(client, request with { Type = "lease-setting-update", Sequence = 1, Invocation = Invocation(50) }, stop.Token);
            Assert.Equal(OutcomeStatus.Succeeded, (await ProviderProtocol.ReadAsync(client, stop.Token)).Status);
            Assert.Equal(50, native.Value); Assert.Equal(1, captures); Assert.False(work.IsCompleted);
            if (disconnect) client.Dispose();
            else
            {
                await ProviderProtocol.WriteAsync(client, request with { Type = "lease-setting-release", Sequence = 2, Invocation = null }, stop.Token);
                var receipt = await ProviderProtocol.ReadAsync(client, stop.Token); Assert.Equal(OutcomeStatus.Succeeded, receipt.Status);
                Assert.Contains("最初", receipt.Reason);
            }
            await work.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(75, native.Value); Assert.Equal(1, native.Disposed);
            Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(request with { CommandId = Guid.NewGuid() }, DateTimeOffset.UtcNow));
        }
    }
    [Fact(Timeout = 10000)]
    public async Task EngineArbitrationRetainsOneBaselineAcrossWinnersAndUnknownPauseDeleteRelease()
    {
        var native = new SyntheticSession(75); var captures = 0;
        var provider = Provider((_, _) => { captures++; return Task.FromResult<IRestorableSettingSession>(native); });
        var executor = new LeaseExecutor(provider); using var fixture = new AutomationFixture(executor);
        fixture.Engine.RegisterCapability(SyntheticBinding()); var output = fixture.Engine.RegisterSource("owned-setting", [Setting]); long sequence = -1;
        using var stop = new CancellationTokenSource();
        var observation = provider.ObserveAsync([new(Setting.CapabilityId, Setting.ResourceId)], update =>
        {
            var values = update.Samples.Select(sample => new Observation(Setting, sample.Quality,
                StateValue.From(RestorableSettingInvocations.Schema, sample.Text!), fixture.Clock.UtcNow)).ToArray();
            if (update.Snapshot) fixture.Engine.ApplySnapshot(output, ++sequence, values);
            else foreach (var value in values) fixture.Engine.Update(output, ++sequence, value);
        }, stop.Token);
        RuleDefinition Rule(string id, ResourceKey condition, int value, int priority = 0, bool enabled = true, long version = 1) => new(id, version, "owner", ResourceScope.Machine,
            RuleCondition.Equal(condition, StateValue.From(Schema, true)), Action(value), priority, enabled);
        fixture.Update(true); // Initial Known true must apply Desired, while original Command behavior is unchanged.
        fixture.Engine.SetRule(Rule("low", Input, 20, 1)); fixture.Engine.SetRule(Rule("high", Noise, 50, 2)); fixture.Engine.StartExecution(new(maximumAttempts: 1));
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(20, native.Value); Assert.Equal(1, captures);
        fixture.Update(true, Noise); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(50, native.Value); Assert.Equal(1, captures);
        fixture.Engine.RemoveRule("high"); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(20, native.Value); Assert.Equal(1, captures);
        fixture.Engine.Update(fixture.Source, ++fixture.Sequence, new(Input, StateQuality.Unknown, null, fixture.Clock.UtcNow));
        await executor.DrainAsync(); Assert.Equal(75, native.Value); Assert.Equal(1, native.Disposed);
        // Re-enable starts a new capture only after the previous real lease has released.
        native = new(75); fixture.Update(true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(2, captures);
        fixture.Engine.SetRule(Rule("low", Input, 20, 1, false, 2)); await executor.DrainAsync(); Assert.Equal(75, native.Value);
        native = new(75); fixture.Engine.SetRule(Rule("third", Input, 30)); await fixture.Engine.WaitForIdleAsync();
        fixture.Engine.RemoveRule("third"); await executor.DrainAsync(); Assert.Equal(75, native.Value);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation);
    }
    [Fact(Timeout = 10000)]
    public async Task ReplacedAcquisitionUsesIdleLeaseSnapshotBeforeOldReceiptWithoutStallingWinner()
    {
        var executor = new ControlledLeaseExecutor(); using var fixture = new AutomationFixture(executor);
        fixture.Engine.RegisterCapability(SyntheticBinding()); var output = fixture.Engine.RegisterSource("owned-setting", [Setting]);
        void Idle(long sequence) => fixture.Engine.Update(output, sequence, new(Setting, StateQuality.Known, StateValue.From(RestorableSettingInvocations.Schema, ""), fixture.Clock.UtcNow));
        fixture.Engine.ApplySnapshot(output, 0, [new(Setting, StateQuality.Known, StateValue.From(RestorableSettingInvocations.Schema, ""), fixture.Clock.UtcNow)]);
        fixture.Engine.SetRule(new("first", 1, "owner", ResourceScope.Machine, RuleCondition.Equal(Input, StateValue.From(Schema, true)), Action(20)));
        fixture.Engine.SetRule(new("second", 1, "owner", ResourceScope.Machine, RuleCondition.Equal(Input, StateValue.From(Schema, true)), Action(20)));
        fixture.Engine.StartExecution(new(maximumAttempts: 1)); fixture.Update(true); var first = await executor.Calls.NextAsync();
        fixture.Engine.RemoveRule("first"); Idle(1); first.Complete(OutcomeStatus.Failed, FailureKind.Permanent);
        var replacement = await executor.Calls.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(replacement.Desired!.Generation > first.Desired!.Generation); replacement.Complete(); await fixture.Engine.WaitForIdleAsync();
    }
    [Fact]
    public async Task RegisteredRestoreActionsStrictImportAndPausedSaveKeepOldCommandsAndExcludeFeedback()
    {
        var modules = BuiltInAutomationPlugins.Create(); var plugin = Assert.Single(modules.OfType<ConditionRestorePlugin>());
        Assert.Equal(5, plugin.Actions.Count()); Assert.All(plugin.Bindings, b => { Assert.True(b.Descriptor.Observe); Assert.True(b.Descriptor.DesiredState); Assert.False(b.Descriptor.Command); });
        Assert.DoesNotContain(plugin.Combinations, c => c.DetectionId is HotkeyCapabilities.Pressed or SystemEventCapabilities.Event);
        Assert.DoesNotContain(new TaskCombination(PowerSchemeStateCapabilities.State, ConditionRestoreCapabilities.PowerScheme), plugin.Combinations);
        Assert.DoesNotContain(new TaskCombination(WindowStateDetectionCapabilities.StateIs, ConditionRestoreCapabilities.WindowState), plugin.Combinations);
        Assert.DoesNotContain(new TaskCombination(ForegroundCapabilities.Active, ConditionRestoreCapabilities.WindowState), plugin.Combinations);
        Assert.DoesNotContain(new TaskCombination(EndpointMuteCapabilities.State, ConditionRestoreCapabilities.Playback), plugin.Combinations);
        var compiler = new AutomationTaskCompiler(); foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        var catalog = new AutomationSnapshot([], compiler.Catalog, [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var detection = new TaskInvocation("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", "Synthetic.exe"));
        var task = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "temporary-playback-volume").Draft with { Detection = detection };
        Assert.True(task.Paused); Assert.Equal(RuleActionKind.Desired, compiler.Compile(task, "owner", ResourceScope.Session("owner", 1)).Action.Kind);
        Assert.Equal(RuleActionKind.Command, compiler.Compile(task with { Action = Invocation() with { CapabilityId = AudioPlaybackCapabilities.Set } }, "owner", ResourceScope.Session("owner", 1)).Action.Kind);
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var imported = new UsageImport(1, recorder.Session.Id, "existing", "合成恢复规则", "合成说明", new(PowerStateCapabilities.State, 1,
            ImmutableDictionary<string, string>.Empty.Add("mode", "ac").Add("threshold", "25")), Invocation());
        Assert.True(UsageAnalysis.Import(JsonSerializer.Serialize(imported), recorder.Session, catalog).Draft!.Paused);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(imported with { Action = Invocation() with { Parameters = Invocation().Parameters.SetItem("percent", "0.5") } }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => ConditionRestoreCapabilities.Validate(new(ConditionRestoreCapabilities.ProgramPriority, 1,
            ImmutableDictionary<string, string>.Empty.Add("executable", Path.Combine(Path.GetTempPath(), "Synthetic.exe") + "\n" + Path.Combine(Path.GetTempPath(), "Other.exe")).Add("priority", "normal").Add("delaySeconds", "0"))));
        var database = Path.Combine(Path.GetTempPath(), "axiom-synthetic-restore-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new SqliteAutomationTaskStore(database); await store.SaveAsync(task, default);
            var saved = Assert.Single(await store.ListAsync());
            Assert.True(saved.Paused); Assert.Equal(task.Action.CapabilityId, saved.Action.CapabilityId);
            Assert.Equal(task.Action.Parameters["percent"], saved.Action.Parameters["percent"]);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(database + suffix); }
    }
    private static RestorableSettingCapabilityProvider Provider(Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<IRestorableSettingSession>> create) =>
        new(ConditionRestoreCapabilities.Playback, create, ConditionRestoreCapabilities.Validate, ConditionRestoreCapabilities.ResourceId);
    private sealed class SyntheticSession : IRestorableSettingSession
    {
        public int Value; public int Disposed; private readonly ConditionalSettingTransaction<int> transaction;
        public SyntheticSession(int value) { Value = value; transaction = new(value, () => Value, target => Value = target, "合成设置"); }
        public Task<RestorableSettingResult> ApplyAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken token) => Task.FromResult(transaction.Apply(int.Parse(parameters["percent"]), token));
        public Task<RestorableSettingResult> ReleaseAsync(CancellationToken token) => Task.FromResult(transaction.Release(token));
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
    }
    private sealed class BlockingSession(BoundedNativeCommand commands, TaskCompletionSource entered, ManualResetEventSlim release) : SerializedRestorableSettingSession(commands)
    {
        public int Disposed; public bool Relinquished;
        protected override RestorableSettingResult ApplyCore(IReadOnlyDictionary<string, string> parameters, CancellationToken token) { entered.SetResult(); release.Wait(); return new(true, "synthetic late result", !Relinquished); }
        protected override RestorableSettingResult ReleaseCore(CancellationToken token) => new(true, "synthetic release", false);
        protected override void RelinquishCore() => Relinquished = true;
        protected override void DisposeCore() => Disposed++;
    }
    private sealed class LeaseExecutor(RestorableSettingCapabilityProvider provider) : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        private readonly object gate = new(); private RestorableSettingCapabilityProvider.Lease? lease; private readonly List<Task> releases = [];
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized)
        {
            if (key != Setting || authorized) return;
            lock (gate) if (lease is { } existing) { lease = null; releases.Add(Task.Run(async () => await existing.DisposeAsync())); }
        }
        public void ReleaseAllDesired() => SetDesiredAuthorization(Setting, 0, false);
        public async Task DrainAsync() { Task[] held; lock (gate) held = releases.ToArray(); await Task.WhenAll(held); }
        public async ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token)
        {
            var invocation = RestorableSettingInvocations.Decode(desired.Target.Text!); RestorableSettingResult result;
            if (lease is null) { var created = await provider.AcquireAsync(invocation, token); lock (gate) lease = created; result = created.Initial; }
            else result = await lease.UpdateAsync(invocation, token);
            return new(result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason, Guid.NewGuid(), FailureKind.Permanent);
        }
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException("restore must be Desired");
    }
    private sealed class ControlledLeaseExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public bool IsReleasedObservation(ResourceKey key, StateValue value) => key == Setting && value.Schema == RestorableSettingInvocations.Schema && value.Text == "";
        public readonly ControlledExecutor Calls = new();
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) { }
        public void ReleaseAllDesired() { }
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => Calls.ApplyDesiredAsync(desired, token);
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException();
    }
}
