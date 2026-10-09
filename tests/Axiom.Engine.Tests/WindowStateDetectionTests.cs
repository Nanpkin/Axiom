using System.Collections.Immutable;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class WindowStateDetectionTests
{
    private static readonly string A = Path.Combine(Path.GetTempPath(), "SyntheticA", "Editor.exe");
    private static readonly string B = Path.Combine(Path.GetTempPath(), "SyntheticB", "Editor.exe");
    private static TaskInvocation Detection(string state = "maximized", string? paths = null) => new(WindowStateDetectionCapabilities.StateIs, 1,
        ImmutableDictionary<string, string>.Empty.Add("executable", paths ?? A).Add("state", state));
    private static WindowStateReading Visible(string? path, WindowVisualState? state = WindowVisualState.Maximized, bool known = true) => new(true, path, known, state);
    private static WindowStateValue For(WindowStateValue[] values, string path, WindowVisualState state) => values.Single(v => v.Executable == path && v.State == state);

    [Theory]
    [InlineData("minimized")]
    [InlineData("maximized")]
    [InlineData("normal")]
    public void ExactPathsAndAnyWindowDetectionCompileWithoutStateChangingFeedback(string state)
    {
        var scope = ResourceScope.Session("synthetic-owner", 1);
        var condition = new WindowStateTaskDetection().Compile(Detection(state, A + "\n" + B), scope);
        Assert.Equal(ConditionKind.Any, condition.Kind); Assert.Equal(2, condition.Dependencies.Count);
        Assert.All(condition.Dependencies, k => { Assert.Equal(scope, k.Scope); Assert.Equal(state, WindowStateDetectionCapabilities.ParseResource(k.ResourceId).State); });
        var plugin = new WindowStateDetectionPlugin(); var binding = Assert.Single(plugin.Bindings);
        Assert.True(binding.Descriptor.Observe); Assert.False(binding.Descriptor.Command); Assert.False(binding.Descriptor.DesiredState);
        Assert.Contains(new TaskCombination(WindowStateDetectionCapabilities.StateIs, KeepAwakeCapabilities.Hold), plugin.Combinations);
        Assert.DoesNotContain(plugin.Combinations, c => c.ActionId is WindowCapabilities.State or WindowMonitorCapabilities.Move);
        Assert.Single(BuiltInAutomationPlugins.Create().OfType<WindowStateDetectionPlugin>());
    }

    [Fact]
    public void MissingNamesRelativePathsScriptsInvalidStatesAndExtraParametersAreRejected()
    {
        foreach (var invocation in new[] { Detection(paths: "Editor.exe"), Detection(state: "fullscreen"), Detection() with { SchemaVersion = 2 },
            Detection() with { Parameters = Detection().Parameters.Remove("executable") },
            Detection() with { Parameters = Detection().Parameters.Add("title", "private text") },
            Detection(paths: Path.Combine(Path.GetTempPath(), "powershell.exe")),
            Detection(paths: string.Join("\n", Enumerable.Range(0, 33).Select(i => Path.Combine(Path.GetTempPath(), $"Synthetic{i}.exe")))) })
            Assert.Throws<ArgumentException>(() => WindowStateDetectionCapabilities.Validate(invocation));
        foreach (var resource in new[] { "normal", "fullscreen|" + A, "normal|Editor.exe", "normal|" + A + "|private" })
            Assert.Throws<ArgumentException>(() => WindowStateDetectionCapabilities.ParseResource(resource));
        var duplicate = WindowStateDetectionCapabilities.Validate(Detection(paths: A + "\n" + A.ToUpperInvariant()));
        Assert.Single(duplicate.Paths);
    }

    [Fact]
    public void MultipleMainWindowsUseAnyStateAndHiddenOrAbsentNeverCountAsNormal()
    {
        var state = new WindowStateProjection([A, B]);
        var initial = state.Changes(); Assert.All(initial, v => { Assert.False(v.Matches); Assert.True(v.Baseline); });
        state.Set(1, Visible(A)); state.Set(2, Visible(A, WindowVisualState.Normal));
        var changes = state.Changes(); Assert.True(For(changes, A, WindowVisualState.Maximized).Matches); Assert.True(For(changes, A, WindowVisualState.Normal).Matches);
        state.Set(1, new(false, A, true, WindowVisualState.Normal));
        Assert.False(Assert.Single(state.Changes()).Matches);
        state.Set(2, new(false, A, true, WindowVisualState.Normal));
        Assert.False(Assert.Single(state.Changes()).Matches);
        Assert.Empty(state.Changes());
        state.Set(3, Visible(B, WindowVisualState.Minimized));
        Assert.Equal(B, Assert.Single(state.Changes()).Executable);
    }

    [Fact]
    public void UnknownUsesThreeValuedOrAndOnlyPossiblePathsAndStatesAreAffected()
    {
        var state = new WindowStateProjection([A, B]); state.Changes();
        state.Set(1, Visible(null, WindowVisualState.Minimized, false));
        var unknown = state.Changes(); Assert.Equal(2, unknown.Length); Assert.All(unknown, s => { Assert.Null(s.Matches); Assert.Equal(WindowVisualState.Minimized, s.State); });
        state.Set(2, Visible(A, WindowVisualState.Minimized));
        var found = Assert.Single(state.Changes()); Assert.True(found.Matches); Assert.True(found.Baseline);
        state.Set(2, new(false, null, true, null)); Assert.Null(Assert.Single(state.Changes()).Matches);
        state.Set(1, Visible(B, WindowVisualState.Minimized, false));
        var narrowed = Assert.Single(state.Changes()); Assert.Equal(A, narrowed.Executable); Assert.False(narrowed.Matches); Assert.True(narrowed.Baseline);
        state.Set(1, Visible(B, null, true));
        var missingState = state.Changes(); Assert.Equal(2, missingState.Length); Assert.All(missingState, s => Assert.Null(s.Matches));
        state.Set(1, Visible(Path.Combine(Path.GetTempPath(), "Unrelated.exe"), null, true));
        Assert.All(state.Changes(), s => { Assert.False(s.Matches); Assert.True(s.Baseline); });
    }

    [Fact]
    public void IdentityReplacementKeepsSameNamedPathsSeparateAndRecoveryPrecedesIndependentEdges()
    {
        var state = new WindowStateProjection([A, B]); state.Set(1, Visible(A, null, false)); state.Changes();
        state.Set(1, Visible(A, WindowVisualState.Normal)); state.Set(2, Visible(B));
        var changes = state.Changes();
        Assert.True(changes[0].Baseline); Assert.False(changes[^1].Baseline); Assert.Equal(B, changes[^1].Executable);
        state.Set(2, Visible(A));
        var replaced = state.Changes(); Assert.True(replaced[0].Matches); Assert.Equal(A, replaced[0].Executable);
        Assert.False(replaced[1].Matches); Assert.Equal(B, replaced[1].Executable);
    }

    [Fact]
    public void CapacityFailureIsExplicitAndHiddenRemovalFreesOneSlot()
    {
        var state = new WindowStateProjection([A]);
        for (var i = 1; i <= 4096; i++) state.Set(i, Visible(A));
        Assert.Throws<IOException>(() => state.Set(4097, Visible(A)));
        state.Set(1, new(false, null, true, null)); state.Set(4097, Visible(A));
        Assert.True(For(state.Changes(), A, WindowVisualState.Maximized).Matches);
    }

    [Fact]
    public void CompiledMultiplePathsUseThreeValuedOrRatherThanGuessingAbsent()
    {
        var scope = ResourceScope.Machine; var condition = new WindowStateTaskDetection().Compile(Detection(paths: A + "\n" + B), scope);
        var source = new SourceSession("synthetic-window-or", 1); var keys = condition.Dependencies.OrderBy(k => k.ResourceId).ToArray();
        ObservedState Sample(ResourceKey key, bool? value) => new(new(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
            value.HasValue ? StateValue.From(new("axiom.bool", 1), value.Value) : null, DateTimeOffset.UtcNow), source, 1, 1);
        TruthValue Evaluate(bool? a, bool? b) => RuleEvaluator.Evaluate(condition,
            new Dictionary<ResourceKey, ObservedState> { [keys[0]] = Sample(keys[0], a), [keys[1]] = Sample(keys[1], b) }).Value;
        Assert.Equal(TruthValue.True, Evaluate(true, null)); Assert.Equal(TruthValue.Unknown, Evaluate(false, null));
        Assert.Equal(TruthValue.False, Evaluate(false, false)); Assert.Equal(TruthValue.True, Evaluate(false, true));
    }

    [Fact(Timeout = 10000)]
    public async Task QueuedDuplicateWindowHintsAreReadOnceWithoutAnotherEnumerationOrPolling()
    {
        using var lifetime = new CancellationTokenSource(); using var api = new FakeApi();
        api.Windows[1] = Visible(A); api.Emit(1, 1, 1);
        var batches = 0;
        var run = WindowStateObservation.ObserveAsync(api, [A], _ => batches++, lifetime.Token);
        Assert.Equal(1, api.Enumerations); Assert.Equal(2, api.Reads); Assert.Equal(1, batches); // One initial read and one deduplicated notification read.
        Assert.False(run.IsCompleted); lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, api.Reads);
    }

    [Fact(Timeout = 10000)]
    public async Task AlreadyCancelledObservationDoesNotEnumerateOrReadTheDesktop()
    {
        using var lifetime = new CancellationTokenSource(); lifetime.Cancel(); using var api = new FakeApi();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowStateObservation.ObserveAsync(api, [A], _ => throw new InvalidOperationException(), lifetime.Token));
        Assert.Equal(0, api.Enumerations); Assert.Equal(0, api.Reads);
    }

    [Fact(Timeout = 10000)]
    public async Task ObservationEnumeratesOnceCoalescesQueuedHintsAndDoesNotReplayRecoveredState()
    {
        using var lifetime = new CancellationTokenSource(); using var api = new FakeApi();
        api.Windows[1] = Visible(A, WindowVisualState.Normal);
        var updates = Channel.CreateUnbounded<WindowStateValue[]>();
        var run = WindowStateObservation.ObserveAsync(api, [A], v => updates.Writer.TryWrite(v), lifetime.Token);
        Assert.All(await updates.Reader.ReadAsync(), v => Assert.True(v.Baseline)); Assert.Equal(1, api.Enumerations);
        api.Windows[1] = Visible(A); api.Emit(1, 1, 1);
        var maximized = await updates.Reader.ReadAsync(); Assert.True(For(maximized, A, WindowVisualState.Maximized).Matches);
        Assert.All(maximized, v => Assert.False(v.Baseline));
        api.Windows[1] = Visible(null, WindowVisualState.Maximized, false); api.Emit(1);
        Assert.Null(Assert.Single(await updates.Reader.ReadAsync()).Matches);
        api.Windows[1] = Visible(A); api.Emit(1);
        Assert.True(Assert.Single(await updates.Reader.ReadAsync()).Baseline);
        Assert.Equal(1, api.Enumerations);
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact(Timeout = 10000)]
    public async Task ProviderKeepsIndependentStateResourcesBaselineFlagsAndCancellationDisposal()
    {
        using var lifetime = new CancellationTokenSource(); var api = new FakeApi(); api.Windows[1] = Visible(A);
        var updates = Channel.CreateUnbounded<ProviderUpdate>(); string[]? selected = null;
        var provider = new WindowStateDetectionCapabilityProvider(paths => { selected = paths; return api; });
        var resources = new[] { new ProviderResource(WindowStateDetectionCapabilities.StateIs, WindowStateDetectionCapabilities.Resource(A, "maximized")),
            new ProviderResource(WindowStateDetectionCapabilities.StateIs, WindowStateDetectionCapabilities.Resource(A, "normal")) };
        var run = provider.ObserveAsync(resources, u => updates.Writer.TryWrite(u), lifetime.Token);
        var connecting = await updates.Reader.ReadAsync(); Assert.True(connecting.Snapshot); Assert.All(connecting.Samples, s => Assert.Equal(StateQuality.Unknown, s.Quality));
        var initial = new[] { await updates.Reader.ReadAsync(), await updates.Reader.ReadAsync() };
        Assert.All(initial, u => Assert.True(u.Snapshot)); Assert.Single(selected!);
        api.Windows[1] = Visible(A, WindowVisualState.Normal); api.Emit(1);
        var next = new[] { await updates.Reader.ReadAsync(), await updates.Reader.ReadAsync() };
        Assert.All(next, u => Assert.False(u.Snapshot));
        Assert.True(Assert.Single(next[0].Samples).Boolean); Assert.EndsWith("|" + A.ToLowerInvariant(), Assert.Single(next[0].Samples).Resource.ResourceId);
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.Equal(1, api.Disposals);
    }

    [Fact(Timeout = 10000)]
    public async Task NotificationFailurePublishesUnknownDisposesAndNeverRetriesOrLeaksErrorIdentity()
    {
        using var lifetime = new CancellationTokenSource(); var api = new FakeApi(); var connects = 0;
        var updates = Channel.CreateUnbounded<ProviderUpdate>();
        var provider = new WindowStateDetectionCapabilityProvider(_ => { connects++; return api; });
        var run = provider.ObserveAsync([new(WindowStateDetectionCapabilities.StateIs, WindowStateDetectionCapabilities.Resource(A, "normal"))],
            u => updates.Writer.TryWrite(u), lifetime.Token);
        await updates.Reader.ReadAsync(); await updates.Reader.ReadAsync(); api.Fail();
        var fault = Assert.Single((await updates.Reader.ReadAsync()).Samples); Assert.Equal(StateQuality.Unknown, fault.Quality);
        Assert.DoesNotContain("synthetic private path", fault.Reason); Assert.Equal(1, connects); Assert.Equal(1, api.Disposals); Assert.False(run.IsCompleted);
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.Equal(1, api.Disposals);
    }

    [Theory(Timeout = 10000)]
    [InlineData("minimized")]
    [InlineData("maximized")]
    [InlineData("normal")]
    public async Task CommandsDoNotReplayInitialOrUnknownRecoveryButNextRealStateEdgeExecutes(string state)
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor);
        var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(WindowStateDetectionCapabilities.StateIs, schema: schema));
        var condition = new WindowStateTaskDetection().Compile(Detection(state), ResourceScope.Machine);
        var source = fixture.Engine.RegisterSource("synthetic-window-state", condition.Dependencies); long sequence = 0;
        Observation Sample(bool? matches) => new(condition.Key!, matches.HasValue ? StateQuality.Known : StateQuality.Unknown,
            matches.HasValue ? StateValue.From(schema, matches.Value) : null, fixture.Clock.UtcNow);
        fixture.Engine.ApplySnapshot(source, ++sequence, [Sample(true)]);
        fixture.Engine.SetRule(new("synthetic-window-command", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true))));
        fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        fixture.Engine.Update(source, ++sequence, Sample(null)); fixture.Engine.ApplySnapshot(source, ++sequence, [Sample(true)]);
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        fixture.Engine.Update(source, ++sequence, Sample(false)); fixture.Engine.Update(source, ++sequence, Sample(true));
        (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task InitialMaximizedConditionHoldsDesiredLeaseAndUnknownAbsenceOrPauseReleasesIt()
    {
        var executor = new LeaseExecutor(); using var fixture = new AutomationFixture(executor);
        var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(WindowStateDetectionCapabilities.StateIs, schema: schema));
        var condition = new WindowStateTaskDetection().Compile(Detection(), ResourceScope.Machine);
        var source = fixture.Engine.RegisterSource("synthetic-window-awake", condition.Dependencies); long sequence = 0;
        var rule = new RuleDefinition("synthetic-window-awake", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        fixture.Engine.SetRule(rule); fixture.Engine.StartExecution();
        Observation Sample(bool? value) => new(condition.Key!, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
            value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow);
        fixture.Engine.ApplySnapshot(source, ++sequence, [Sample(true)]); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        fixture.Update(true, AutomationFixture.Output);
        fixture.Engine.Update(source, ++sequence, Sample(false)); Assert.False(executor.Authorized);
        fixture.Engine.ApplySnapshot(source, ++sequence, [Sample(true)]); Assert.True(executor.Authorized);
        fixture.Engine.Update(source, ++sequence, Sample(null)); Assert.False(executor.Authorized);
        fixture.Engine.ApplySnapshot(source, ++sequence, [Sample(true)]); Assert.True(executor.Authorized);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", ResourceScope.Machine, condition, rule.Action, enabled: false)); Assert.False(executor.Authorized);
    }

    private sealed class FakeApi : IWindowStateApi
    {
        public readonly Dictionary<nint, WindowStateReading> Windows = [];
        private readonly Channel<nint> events = Channel.CreateUnbounded<nint>();
        public ChannelReader<nint> Events => events.Reader;
        public int Enumerations, Disposals, Reads;
        public void Enumerate(Action<nint> visit) { Enumerations++; foreach (var window in Windows.Keys.ToArray()) visit(window); }
        public WindowStateReading Read(nint window) { Reads++; return Windows.GetValueOrDefault(window) ?? new(false, null, true, null); }
        public void Emit(params nint[] windows) { foreach (var window in windows) Assert.True(events.Writer.TryWrite(window)); }
        public void Fail() => events.Writer.TryComplete(new IOException("synthetic private path"));
        public void Dispose() { Disposals++; events.Writer.TryComplete(); }
    }
    private sealed class LeaseExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public bool Authorized;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) => Authorized = authorized;
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic lease", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException("Unexpected command.");
    }
}
