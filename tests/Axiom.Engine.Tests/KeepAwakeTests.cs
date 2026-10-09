using System.Collections.Immutable;
using System.IO.Pipes;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.SessionAgent;
using static Axiom.Engine.Tests.AutomationFixture;

namespace Axiom.Engine.Tests;

public sealed class KeepAwakeTests
{
    private static TaskInvocation Invocation(string target = "system") => new(KeepAwakeCapabilities.Hold, 1,
        ImmutableDictionary<string, string>.Empty.Add("target", target));

    [Fact]
    public async Task SavedConfigurationCompilesToDesiredAndRejectsCommandsTimeWindowAndBadTargets()
    {
        var plugin = new KeepAwakePlugin(); var compiler = new AutomationTaskCompiler();
        compiler.Register(new ProcessRunningTaskDetection()); compiler.Register(plugin.Actions.Single());
        var task = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "synthetic download", false,
            new("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", "transfer.exe\nmeeting.exe")), Invocation("display"));
        var path = Path.Combine(Path.GetTempPath(), "axiom-power-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new SqliteAutomationTaskStore(path);
            var saved = await store.SaveAsync(task, default); var loaded = (await store.ListAsync()).Single();
            Assert.Equal(saved.Id, loaded.Id); Assert.Equal(saved.Version, loaded.Version);
            Assert.Equal("display", loaded.Action.Parameters["target"]);
            var rule = compiler.Compile(loaded, "synthetic-owner", ResourceScope.Session("synthetic-owner", 1));
            Assert.Equal(RuleActionKind.Desired, rule.Action.Kind); Assert.Equal("display", rule.Action.Key.ResourceId);
            Assert.True(rule.Action.Value.Boolean); Assert.Equal(2, rule.Condition.Dependencies.Count);
            Assert.False(plugin.Bindings.Single().Descriptor.Command); Assert.True(plugin.Bindings.Single().Descriptor.Observe);
            Assert.False(compiler.Catalog.Single(c => c.Id == KeepAwakeCapabilities.Hold).SupportsTimeWindow);
            Assert.Throws<ArgumentException>(() => compiler.Compile(loaded with { Action = Invocation("invalid") }, "synthetic-owner", rule.Scope));
            Assert.Throws<ArgumentException>(() => KeepAwakeCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add("extra", "x") }));
            Assert.Equal(OutcomeStatus.Rejected, (await new KeepAwakeCapabilityProvider().InvokeAsync(Invocation(), default)).Status);
            Assert.Contains(new TaskCombination(WindowPresenceCapabilities.Visible, KeepAwakeCapabilities.Hold), plugin.Combinations);
            Assert.Contains(new TaskCombination(ForegroundCapabilities.Active, KeepAwakeCapabilities.Hold), plugin.Combinations);
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact(Timeout = 10000)]
    public async Task SharedNativeRequestOutlivesOneLeaseAndLastReleasePublishesFalse()
    {
        var native = new SyntheticRequests(); var provider = new KeepAwakeCapabilityProvider(native.Acquire);
        var samples = new List<ProviderSample>(); using var stop = new CancellationTokenSource();
        var observer = provider.ObserveAsync([new(KeepAwakeCapabilities.Hold, "system"), new(KeepAwakeCapabilities.Hold, "display")], u => samples.AddRange(u.Samples), stop.Token);
        Assert.All(samples, s => Assert.False(s.Boolean));
        var first = provider.AcquireDesired(Invocation(), default); var second = provider.AcquireDesired(Invocation(), default);
        using var display = provider.AcquireDesired(Invocation("display"), default);
        Assert.Equal(2, native.Created); Assert.Equal(2, native.Active);
        first.Dispose(); first.Dispose(); Assert.Equal(2, native.Active);
        second.Dispose(); Assert.Equal(1, native.Active);
        Assert.False(samples.Last(s => s.Resource.ResourceId == "system").Boolean);
        Assert.True(samples.Last(s => s.Resource.ResourceId == "display").Boolean);
        display.Dispose(); Assert.Equal(0, native.Active);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
        Assert.Equal(2, native.Disposed);
    }

    [Fact(Timeout = 10000)]
    public async Task CancellationAndNativeFailureNeverPublishHeldOwnership()
    {
        var created = 0; var disposed = 0; using var cancellation = new CancellationTokenSource();
        var provider = new KeepAwakeCapabilityProvider(_ => { created++; cancellation.Cancel(); return new CallbackLease(() => disposed++); });
        Assert.Throws<OperationCanceledException>(() => provider.AcquireDesired(Invocation(), cancellation.Token));
        Assert.Equal(1, created); Assert.Equal(1, disposed);
        var samples = new List<ProviderSample>(); using var stop = new CancellationTokenSource();
        var observer = provider.ObserveAsync([new(KeepAwakeCapabilities.Hold, "system")], u => samples.AddRange(u.Samples), stop.Token);
        Assert.False(samples.Single().Boolean);
        var failing = new KeepAwakeCapabilityProvider(_ => throw new IOException("synthetic power failure"));
        Assert.Throws<IOException>(() => failing.AcquireDesired(Invocation(), default));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeerDisconnectAndAgentShutdownReleaseRealLeaseStream(bool stopAgent)
    {
        var native = new SyntheticRequests(); var provider = new KeepAwakeCapabilityProvider(native.Acquire);
        var name = "axiom-synthetic-power-" + Guid.NewGuid().ToString("N");
        using var agent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(agent.Token); await client.ConnectAsync(agent.Token); await accepted;
        var request = new ProviderMessage(1, "lease", "synthetic-lease", DateTimeOffset.UtcNow.AddSeconds(2), Invocation: Invocation("display"));
        ProviderProtocol.Validate(request, DateTimeOffset.UtcNow);
        var leaseTask = SessionDesiredLeaseStream.RunAsync(server, request, provider, agent.Token);
        var receipt = await ProviderProtocol.ReadAsync(client, agent.Token);
        Assert.Equal(OutcomeStatus.Succeeded, receipt.Status); Assert.Equal(1, native.Active);
        // The initialization deadline is not the lifetime of the Desired State.
        await Task.Delay(2100, agent.Token); Assert.False(leaseTask.IsCompleted); Assert.Equal(1, native.Active);
        if (stopAgent) agent.Cancel(); else client.Dispose();
        await leaseTask.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(0, native.Active); Assert.Equal(1, native.Disposed);
    }

    [Fact]
    public void LeaseProtocolIsNarrowAndRejectsCommandIdentifiersAndUnknownCapabilities()
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ProviderMessage(1, "lease", "synthetic", now.AddSeconds(3), Invocation: Invocation());
        ProviderProtocol.Validate(message, now);
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(message with { CommandId = Guid.NewGuid() }, now));
        Assert.Throws<ArgumentException>(() => ProviderProtocol.Validate(message with { Invocation = Invocation() with { CapabilityId = "program.start" } }, now));
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(message with { DeadlineUtc = now.AddSeconds(46) }, now));
    }

    [Fact(Timeout = 10000)]
    public async Task EngineKeepsConfirmedRequestAcrossWinningRuleChangesAndReleasesForUnknownPauseDeleteStop()
    {
        var executor = new LeasedExecutor(); using var fixture = new AutomationFixture(executor);
        fixture.Engine.SetRule(fixture.Rule("first")); fixture.Engine.SetRule(fixture.Rule("second")); fixture.Engine.StartExecution(new(maximumAttempts: 1));
        fixture.Update(true); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        fixture.Update(true, Output); Assert.Equal(DecisionStatus.Confirmed, fixture.Engine.GetDecision(Output).Status);
        Assert.True(executor.Authorized); Assert.Equal(1, executor.Applies);
        fixture.Engine.RemoveRule("first"); Assert.True(executor.Authorized); Assert.Equal(1, executor.Applies);
        fixture.Engine.Update(fixture.Source, ++fixture.Sequence, new(Input, StateQuality.Unknown, null, fixture.Clock.UtcNow));
        Assert.False(executor.Authorized);
        fixture.Update(true); Assert.True(executor.Authorized);
        var active = fixture.Rule("second");
        fixture.Engine.SetRule(new(active.Id, active.Version + 1, active.Principal, active.Scope, active.Condition, active.Action, enabled: false)); Assert.False(executor.Authorized);
        fixture.Engine.SetRule(fixture.Rule("second", version: 3)); Assert.True(executor.Authorized);
        fixture.Engine.RemoveRule("second"); Assert.False(executor.Authorized);
        fixture.Engine.SetRule(fixture.Rule("third")); Assert.True(executor.Authorized);
        fixture.Engine.Stop(); Assert.False(executor.Authorized); Assert.True(executor.AllReleased);
    }

    [Fact(Timeout = 10000)]
    public async Task TargetUnknownAndInputSourceDisconnectRevokeEvenRetainedRulesWithoutRetryLoop()
    {
        var executor = new LeasedExecutor(); using var fixture = new AutomationFixture(executor);
        fixture.Engine.SetRule(fixture.Rule(keep: true)); fixture.Engine.StartExecution(new(maximumAttempts: 1));
        fixture.Update(true); await fixture.Engine.WaitForIdleAsync(); fixture.Update(true, Output); Assert.True(executor.Authorized);
        fixture.Engine.Update(fixture.Source, ++fixture.Sequence, new(Output, StateQuality.Unknown, null, fixture.Clock.UtcNow));
        Assert.False(executor.Authorized); Assert.Equal(1, executor.Applies);
        fixture.Scheduler.Advance(TimeSpan.FromMinutes(1)); Assert.False(executor.Authorized); Assert.Equal(1, executor.Applies);
        fixture.Engine.Disconnect(fixture.Source); Assert.False(executor.Authorized); Assert.Equal(1, executor.Applies);
    }

    [Fact(Timeout = 10000)]
    public async Task ReplacedHandshakeCanUseOwnershipFalseThatArrivedBeforeOldReceipt()
    {
        var executor = new ControlledLeasedExecutor(); using var fixture = new AutomationFixture(executor);
        fixture.Engine.SetRule(fixture.Rule("first")); fixture.Engine.SetRule(fixture.Rule("second"));
        fixture.Engine.StartExecution(new(maximumAttempts: 1)); fixture.Update(true);
        var first = await executor.Calls.NextAsync();
        fixture.Engine.RemoveRule("first");
        for (var i = 0; i < 100 && !first.Token.IsCancellationRequested; i++) await Task.Delay(10);
        Assert.True(first.Token.IsCancellationRequested);
        fixture.Update(false, Output); // Agent closed old ownership before the old executor returned.
        first.Complete(OutcomeStatus.Failed, FailureKind.Permanent);
        var replacement = await executor.Calls.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(replacement.Desired!.Generation > first.Desired!.Generation);
        replacement.Complete(); await fixture.Engine.WaitForIdleAsync(); fixture.Update(true, Output);
        Assert.Equal(DecisionStatus.Confirmed, fixture.Engine.GetDecision(Output).Status);
    }

    [Fact(Timeout = 10000)]
    public async Task ReleasingPhysicalObservationRevokesLeaseBeforeReacquiringBaseline()
    {
        var clock = new FakeClock(); var scheduler = new FakeScheduler(clock); var executor = new LeasedExecutor();
        using var engine = new EngineHost(clock, scheduler, executor); engine.Start(); engine.EnableObservationSafety();
        engine.ConfigureAutomation(new FakeAuthorization(), new SequentialIds());
        engine.RegisterCapability(Binding("input")); engine.RegisterCapability(Binding("output"));
        engine.SetRule(new("synthetic-rule", 1, "owner", ResourceScope.Machine, RuleCondition.Equal(Input, StateValue.From(Schema, true)),
            RuleAction.Desired(Output, StateValue.From(Schema, true))));
        var input = engine.RegisterSource("synthetic-input", [Input]); var output = engine.RegisterSource("synthetic-output", [Output]);
        var plan = engine.GetObservationPlan();
        Assert.True(engine.TryApplyObservationSnapshot(plan.Version, output, 0, Observe(Output, false), plan.Requirements));
        Assert.True(engine.TryApplyObservationSnapshot(plan.Version, input, 0, Observe(Input, true), plan.Requirements));
        engine.StartExecution(new(maximumAttempts: 1)); await engine.WaitForIdleAsync();
        Assert.True(engine.TryApplyObservationSnapshot(plan.Version, output, 1, Observe(Output, true), plan.Requirements));
        Assert.True(executor.Authorized);
        Assert.True(engine.TryBeginObservationRelease(Input, recovery: true)); Assert.False(executor.Authorized);
        Assert.True(engine.TryApplyObservationSnapshot(plan.Version, input, 1, Observe(Input, true), plan.Requirements));
        Assert.True(executor.Authorized);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameGenerationUnknownOrTimeoutKeepsObservationFenceAndDoesNotImmediatelyReapply(bool timeout)
    {
        var executor = new ControlledLeasedExecutor(); using var fixture = new AutomationFixture(executor);
        fixture.Engine.SetRule(fixture.Rule()); fixture.Engine.StartExecution(new(maximumAttempts: 1)); fixture.Update(true);
        var call = await executor.Calls.NextAsync(); var generation = call.Desired!.Generation;
        if (timeout)
        {
            fixture.Scheduler.Advance(TimeSpan.FromSeconds(11));
            for (var i = 0; i < 100 && !call.Token.IsCancellationRequested; i++) await Task.Delay(10);
            Assert.True(call.Token.IsCancellationRequested);
        }
        call.Complete(timeout ? OutcomeStatus.Failed : OutcomeStatus.Unknown, FailureKind.Permanent);
        await fixture.Engine.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(generation, fixture.Engine.GetDecision(Output).Generation);
        Assert.Equal(1, executor.Calls.Count); Assert.Equal(0, fixture.Engine.PendingExecutions);
        Assert.Equal(DecisionStatus.AwaitingObservation, fixture.Engine.GetDecision(Output).Status);
        fixture.Scheduler.Advance(TimeSpan.FromMinutes(1));
        fixture.Update(false, Output); fixture.Update(false, Output);
        await fixture.Engine.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, executor.Calls.Count); Assert.Equal(0, fixture.Engine.PendingExecutions);
    }

    private sealed class ControlledLeasedExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public readonly ControlledExecutor Calls = new();
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) { }
        public void ReleaseAllDesired() { }
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => Calls.ApplyDesiredAsync(desired, token);
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class LeasedExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public bool Authorized; public bool AllReleased; public int Applies;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) => Authorized = authorized;
        public void ReleaseAllDesired() { Authorized = false; AllReleased = true; }
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token)
        { Applies++; return ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic owned request", Guid.NewGuid())); }
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException("Desired must not become a command.");
    }
    private sealed class SyntheticRequests
    {
        public int Created, Active, Disposed;
        public IDisposable Acquire(string target)
        { Interlocked.Increment(ref Created); Interlocked.Increment(ref Active); return new CallbackLease(() => { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Disposed); }); }
    }
    private sealed class CallbackLease(Action release) : IDisposable
    {
        private Action? callback = release;
        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}
