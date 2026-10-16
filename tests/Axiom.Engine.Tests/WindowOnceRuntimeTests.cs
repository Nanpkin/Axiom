using System.Collections.Immutable;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.ServiceHost;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowOnceRuntimeTests
{
    private static AutomationTask Rule(string executable = @"C:\Apps\window.exe", int retries = 3, string delay = "0") =>
        new(Guid.NewGuid().ToString("D"), 0, "时段任务", false,
            new(WindowOnceCapabilities.Due, 1, WindowOnceCapabilities.Descriptor.Fields.ToImmutableDictionary(f => f.Id, f => f.DefaultValue!)
                .SetItem("retryMinutes", "1").SetItem("maxRetries", retries.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            new("program.start", 1, ProgramCapabilities.Describe(true).Fields.Where(f => f.DefaultValue is not null)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).SetItem("executable", executable).SetItem("delaySeconds", delay)));
    private static ManualTimeProvider Clock()
    {
        var time = new ManualTimeProvider();
        var local = new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Unspecified);
        time.JumpUtc(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, time.LocalTimeZone)) - time.GetUtcNow()); return time;
    }
    private static DateOnly Date(ManualTimeProvider time) => WindowOnceCapabilities.Validate(Rule().Detection).WindowDate(time.GetUtcNow(), time.LocalTimeZone)!.Value;
    private static string DirectoryPath() { var path = Path.Combine(Path.GetTempPath(), "axiom-window-once-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static async Task Until(Func<Task<bool>> check)
    { for (var i = 0; i < 500; i++) { if (await check()) return; await Task.Delay(10); } throw new TimeoutException("Window runtime did not reach expected state."); }
    private static async Task State(SqliteAutomationTaskStore store, AutomationTask task, DateOnly date, string status, int attempts) =>
        await Until(async () => await store.ReadWindowOnceAttemptAsync(task.Id, date) is { } state && state.Status == status && state.Attempts == attempts);
    private static PluginTaskRuntime Runtime(SqliteAutomationTaskStore store, ProviderFixture provider, AutomationFixture activity, ManualTimeProvider time) =>
        new([new ProgramPlugin(), new WindowOncePlugin()], store, provider.Sid, activity.Engine, windowTime: time);

    [Fact]
    public async Task DurableAttemptClaimsAreAtomicVersionedAndPreserveSuccessFailureAndUnknownAcrossReopen()
    {
        var path = DirectoryPath(); var db = Path.Combine(path, "tasks.db"); var time = Clock(); var day = Date(time);
        try
        {
            AutomationTask task;
            using (var first = new SqliteAutomationTaskStore(db))
            using (var second = new SqliteAutomationTaskStore(db))
            {
                task = await first.SaveAsync(Rule(retries: 1), default); var id = Guid.NewGuid();
                var claimed = await Task.WhenAll(Task.Run(() => first.BeginWindowOnceAttemptAsync(task.Id, task.Version, day, id, time.GetUtcNow(), default)),
                    Task.Run(() => second.BeginWindowOnceAttemptAsync(task.Id, task.Version, day, id, time.GetUtcNow(), default)));
                Assert.Single(claimed, value => value);
                await first.CompleteWindowOnceAttemptAsync(task.Id, day, id, OutcomeStatus.Failed, time.GetUtcNow(), 1);
                Assert.False(await second.BeginWindowOnceAttemptAsync(task.Id, task.Version, day, Guid.NewGuid(), time.GetUtcNow(), default));
                time.Advance(TimeSpan.FromMinutes(1)); id = Guid.NewGuid();
                Assert.True(await second.BeginWindowOnceAttemptAsync(task.Id, task.Version, day, id, time.GetUtcNow(), default));
                await second.CompleteWindowOnceAttemptAsync(task.Id, day, id, OutcomeStatus.Rejected, time.GetUtcNow(), 1);
                time.Advance(TimeSpan.FromMinutes(1));
                Assert.False(await first.BeginWindowOnceAttemptAsync(task.Id, task.Version, day, Guid.NewGuid(), time.GetUtcNow(), default));
                id = Guid.NewGuid(); Assert.True(await first.BeginWindowOnceAttemptAsync(task.Id, task.Version, day.AddDays(1), id, time.GetUtcNow(), default));
                await first.CompleteWindowOnceAttemptAsync(task.Id, day.AddDays(1), id, OutcomeStatus.Succeeded, time.GetUtcNow(), 1);
                id = Guid.NewGuid(); Assert.True(await first.BeginWindowOnceAttemptAsync(task.Id, task.Version, day.AddDays(2), id, time.GetUtcNow(), default));
                await first.CompleteWindowOnceAttemptAsync(task.Id, day.AddDays(2), id, OutcomeStatus.Unknown, time.GetUtcNow(), 1);
                Assert.True(await first.BeginWindowOnceAttemptAsync(task.Id, task.Version, day.AddDays(3), Guid.NewGuid(), time.GetUtcNow(), default)); // interrupted attempt
                task = await first.SaveAsync(task with { Title = "edited" }, default);
                Assert.False(await first.BeginWindowOnceAttemptAsync(task.Id, task.Version - 1, day.AddDays(4), Guid.NewGuid(), time.GetUtcNow(), default));
                task = await first.SaveAsync(task with { Paused = true }, default);
                Assert.False(await first.BeginWindowOnceAttemptAsync(task.Id, task.Version, day.AddDays(4), Guid.NewGuid(), time.GetUtcNow(), default));
            }
            using var reopened = new SqliteAutomationTaskStore(db);
            foreach (var offset in new[] { 1, 2, 3 }) Assert.False(await reopened.BeginWindowOnceAttemptAsync(task.Id, task.Version, day.AddDays(offset), Guid.NewGuid(), time.GetUtcNow(), default));
            Assert.Equal("succeeded", (await reopened.ReadWindowOnceAttemptAsync(task.Id, day.AddDays(1)))!.Status);
            Assert.Equal("unknown", (await reopened.ReadWindowOnceAttemptAsync(task.Id, day.AddDays(2)))!.Status);
            Assert.Equal("started", (await reopened.ReadWindowOnceAttemptAsync(task.Id, day.AddDays(3)))!.Status);
            task = await reopened.SaveAsync(task with { Paused = false }, default);
            var cancelled = Guid.NewGuid(); Assert.True(await reopened.BeginWindowOnceAttemptAsync(task.Id, task.Version, day.AddDays(4), cancelled, time.GetUtcNow(), default));
            await reopened.AbandonUnsentWindowOnceAttemptAsync(task.Id, day.AddDays(4), cancelled, time.GetUtcNow());
            Assert.Null(await reopened.ReadWindowOnceAttemptAsync(task.Id, day.AddDays(4)));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact(Timeout = 10000)]
    public async Task LateBootAndClockNotificationsOfferImmediatelyButOnlyBoundaryDeadlinesWakeIdleSource()
    {
        var time = new ManualTimeProvider(); time.JumpUtc(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero) - time.GetUtcNow());
        var changes = Channel.CreateBounded<byte>(1); var events = Channel.CreateUnbounded<decimal>(); var deadlines = Channel.CreateUnbounded<TimeSpan>();
        time.TimerCreated = d => deadlines.Writer.TryWrite(d);
        using var cancel = new CancellationTokenSource(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = WindowOnceEventSource.RunAsync([new(360, 1080, 127)], changes.Reader, (_, count) => events.Writer.TryWrite(count), cancel.Token, time, () => TimeZoneInfo.Utc);
        Assert.Equal(1m, await events.Reader.ReadAsync(timeout.Token)); Assert.Equal(TimeSpan.FromHours(10), await deadlines.Reader.ReadAsync(timeout.Token));
        time.Advance(TimeSpan.FromMinutes(5)); Assert.False(events.Reader.TryRead(out _)); Assert.False(deadlines.Reader.TryRead(out _));
        time.JumpUtc(TimeSpan.FromHours(1)); changes.Writer.TryWrite(1);
        Assert.Equal(2m, await events.Reader.ReadAsync(timeout.Token)); Assert.Equal(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(55), await deadlines.Reader.ReadAsync(timeout.Token));
        time.Advance(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(55)); Assert.Equal(TimeSpan.FromHours(12), await deadlines.Reader.ReadAsync(timeout.Token)); Assert.False(events.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromHours(12)); Assert.Equal(3m, await events.Reader.ReadAsync(timeout.Token));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.All(time.Timers, t => Assert.Null(t.At));
        var ended = Channel.CreateBounded<byte>(1); ended.Writer.Complete();
        await Assert.ThrowsAsync<ChannelClosedException>(() => WindowOnceEventSource.RunAsync([new(360, 1080, 127)], ended.Reader, (_, _) => { }, default, time, () => TimeZoneInfo.Utc));
        Assert.All(time.Timers, t => Assert.Null(t.At));
    }

    [Fact(Timeout = 10000)]
    public async Task EngineFreezesAcceptedEventOriginWhileBusyNewerWindowUpdatesCannotReplaceItsStamp()
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor);
        var schema = new ValueSchema("axiom.event-count", 1); var key = new ResourceKey("calendar", "test", ResourceScope.Machine);
        fixture.Engine.RegisterCapability(new(new("calendar", schema, schema, ScopeKind.Machine, true, false, false, ExecutionLocation.Service,
            "test", true, "test", false, TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation,
            EventFidelity.EdgeSensitive, TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules", "test", []), ScalarKind.Number, ScalarKind.Number));
        var source = fixture.Engine.RegisterSource("calendar", [key]); fixture.Engine.ApplySnapshot(source, 0, [new(key, StateQuality.Known, StateValue.From(schema, 0m), fixture.Clock.UtcNow)]);
        fixture.Engine.SetRule(new("window", 1, "owner", ResourceScope.Machine, RuleCondition.Event(key, schema),
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)), timing: new(minimumDuration: TimeSpan.FromSeconds(1))));
        fixture.Engine.StartExecution(); var first = StateValue.From(schema, 10001m);
        fixture.Engine.Update(source, 1, new(key, StateQuality.Known, first, fixture.Clock.UtcNow));
        fixture.Engine.Update(source, 2, new(key, StateQuality.Known, StateValue.From(schema, 11001m), fixture.Clock.UtcNow));
        fixture.Scheduler.Advance(TimeSpan.FromSeconds(1)); var call = await executor.NextAsync();
        var origin = Assert.Single(call.Command!.Origins); Assert.Equal("window", origin.RuleId); Assert.Equal(1, origin.RuleVersion);
        Assert.Equal(first, origin.EventStamp); Assert.Equal(key, origin.EventSource); call.Complete(); await fixture.Engine.WaitForIdleAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task SameWindowRulesAreIndependentAndSucceededTasksDoNotRepeatAfterEditPauseOrRuntimeRestart()
    {
        var path = DirectoryPath(); var time = Clock(); var date = Date(time);
        try
        {
            using var store = new SqliteAutomationTaskStore(Path.Combine(path, "tasks.db"));
            var first = await store.SaveAsync(Rule(@"C:\Apps\first.exe"), default); var second = await store.SaveAsync(Rule(@"C:\Apps\second.exe"), default);
            await using var provider = new ProviderFixture(); using var activity = new AutomationFixture();
            await using (var runtime = Runtime(store, provider, activity, time))
            {
                await runtime.InitializeAsync(); await provider.NextCommand(); await provider.NextCommand();
                await State(store, first, date, "succeeded", 1); await State(store, second, date, "succeeded", 1);
                first = await runtime.SaveAsync(first with { Title = "edited" }, default);
                first = await runtime.SaveAsync(first with { Paused = true }, default); first = await runtime.SaveAsync(first with { Paused = false }, default);
                await Until(async () => (await runtime.ReadAsync(default)).Observations.Count(o => o.Value.Contains("本时段动作已成功", StringComparison.Ordinal)) == 2); await runtime.EnsureSafeToStopAsync(default);
            }
            await using (var runtime = Runtime(store, provider, activity, time))
            { await runtime.InitializeAsync(); await Until(async () => (await runtime.ReadAsync(default)).Observations.Count(o => o.Value.Contains("本时段动作已成功", StringComparison.Ordinal)) == 2); await runtime.EnsureSafeToStopAsync(default); }
            Assert.Equal(2, provider.Calls); Assert.All(time.Timers, timer => Assert.Null(timer.At));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact(Timeout = 20000)]
    public async Task ExplicitFailuresRetryOnlyAtDurableDeadlinesAndSameResourceEditKeepsRemainingWaitAndLimit()
    {
        var path = DirectoryPath(); var time = Clock(); var date = Date(time);
        try
        {
            using var store = new SqliteAutomationTaskStore(Path.Combine(path, "tasks.db")); var task = await store.SaveAsync(Rule(retries: 2), default);
            await using var provider = new ProviderFixture(OutcomeStatus.Failed, OutcomeStatus.Rejected, OutcomeStatus.Failed); using var activity = new AutomationFixture();
            await using var runtime = Runtime(store, provider, activity, time); await runtime.InitializeAsync(); await provider.NextCommand();
            await State(store, task, date, "failed", 1); await Until(() => Task.FromResult(time.Timers.Any(t => t.At.HasValue)));
            time.Advance(TimeSpan.FromSeconds(30)); task = await runtime.SaveAsync(task with { Title = "edited while waiting" }, default);
            await Until(() => Task.FromResult(time.Timers.Any(t => t.At.HasValue && t.LastDue == TimeSpan.FromSeconds(30))));
            Assert.Equal(1, provider.Calls);
            await provider.SetWindowQualityAsync(false);
            await Until(async () => (await runtime.ReadAsync(default)).Observations.Any(o => o.Value == "synthetic unavailable"));
            await Until(() => Task.FromResult(time.Timers.All(t => t.At is null)));
            task = await runtime.SaveAsync(task with { Title = "edited while provider unknown" }, default);
            time.Advance(TimeSpan.FromSeconds(30)); Assert.Equal(1, provider.Calls); Assert.All(time.Timers, timer => Assert.Null(timer.At));
            await provider.SetWindowQualityAsync(true);
            await provider.NextCommand(); await State(store, task, date, "failed", 2);
            task = await runtime.SaveAsync(task with { Detection = task.Detection with { Parameters = task.Detection.Parameters.SetItem("maxRetries", "1") } }, default);
            await Until(() => Task.FromResult(time.Timers.All(t => t.At is null)));
            task = await runtime.SaveAsync(task with { Detection = task.Detection with { Parameters = task.Detection.Parameters.SetItem("maxRetries", "2") } }, default);
            await Until(() => Task.FromResult(time.Timers.Any(t => t.At.HasValue))); time.Advance(TimeSpan.FromMinutes(1)); await provider.NextCommand();
            await State(store, task, date, "failed", 3); await Until(() => Task.FromResult(time.Timers.All(t => t.At is null)));
            time.Advance(TimeSpan.FromHours(1)); Assert.Equal(3, provider.Calls); await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(path, true); }
        // An unavailable invocation listener is a known unsent failure even when the
        // already-connected observation stream remains healthy.
        path = DirectoryPath(); time = Clock(); date = Date(time);
        try
        {
            using var store = new SqliteAutomationTaskStore(Path.Combine(path, "tasks.db")); var task = await store.SaveAsync(Rule(), default);
            await using var provider = new ProviderFixture() { PauseAcceptingAfterFirstConnection = true }; using var activity = new AutomationFixture();
            await using var runtime = Runtime(store, provider, activity, time); await runtime.InitializeAsync(); await State(store, task, date, "failed", 1);
            Assert.Equal(0, provider.Calls); Assert.True((await runtime.ReadAsync(default)).DetectionAvailable);
            await Until(() => Task.FromResult(time.Timers.Any(t => t.At.HasValue))); provider.AcceptAgain.TrySetResult();
            time.Advance(TimeSpan.FromMinutes(1)); await provider.NextCommand(); await State(store, task, date, "succeeded", 2);
            await provider.SetWindowQualityAsync(false);
            await Until(async () => (await runtime.ReadAsync(default)).Observations.Any(o => o.Value == "synthetic unavailable"));
            await provider.SetWindowQualityAsync(true);
            await Until(async () => (await runtime.ReadAsync(default)).Observations.Any(o => o.Value.Contains("本时段动作已成功", StringComparison.Ordinal)));
            Assert.Equal(1, provider.Calls);
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact(Timeout = 20000)]
    public async Task PauseResumePreservesBackoffAndClockRollbackRearmsOneDeadlineWithoutEarlyRetry()
    {
        var path = DirectoryPath(); var time = Clock(); var date = Date(time);
        try
        {
            using var store = new SqliteAutomationTaskStore(Path.Combine(path, "tasks.db")); var task = await store.SaveAsync(Rule(), default);
            await using var provider = new ProviderFixture(OutcomeStatus.Failed, OutcomeStatus.Succeeded); using var activity = new AutomationFixture();
            await using var runtime = Runtime(store, provider, activity, time); await runtime.InitializeAsync(); await provider.NextCommand(); await State(store, task, date, "failed", 1);
            await Until(() => Task.FromResult(time.Timers.Any(t => t.At.HasValue)));
            task = await runtime.SaveAsync(task with { Paused = true }, default); await Until(() => Task.FromResult(time.Timers.All(t => t.At is null)));
            task = await runtime.SaveAsync(task with { Paused = false }, default); await Until(() => Task.FromResult(time.Timers.Any(t => t.At.HasValue)));
            time.JumpUtc(TimeSpan.FromMinutes(-10)); time.Advance(TimeSpan.FromMinutes(1));
            await Until(() => Task.FromResult(time.Timers.Any(t => t.At.HasValue && t.LastDue == TimeSpan.FromMinutes(10)))); Assert.Equal(1, provider.Calls);
            time.Advance(TimeSpan.FromMinutes(10)); await provider.NextCommand(); await State(store, task, date, "succeeded", 2);
            await runtime.EnsureSafeToStopAsync(default); Assert.Equal(2, provider.Calls);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact(Timeout = 20000)]
    public async Task LostReceiptRemainsUnknownAcrossRestartAndNeverCreatesAutomaticRetryDeadline()
    {
        var path = DirectoryPath(); var time = Clock(); var date = Date(time);
        try
        {
            using var store = new SqliteAutomationTaskStore(Path.Combine(path, "tasks.db")); var task = await store.SaveAsync(Rule(), default);
            await using var provider = new ProviderFixture((OutcomeStatus?)null); using var activity = new AutomationFixture();
            await using (var runtime = Runtime(store, provider, activity, time))
            { await runtime.InitializeAsync(); await provider.NextCommand(); await State(store, task, date, "unknown", 1); await runtime.EnsureSafeToStopAsync(default); }
            time.Advance(TimeSpan.FromHours(1));
            await using (var runtime = Runtime(store, provider, activity, time))
            { await runtime.InitializeAsync(); await Until(async () => (await runtime.ReadAsync(default)).Observations.Any(o => o.Value.Contains("结果未确认", StringComparison.Ordinal))); await runtime.EnsureSafeToStopAsync(default); }
            Assert.Equal(1, provider.Calls); Assert.All(time.Timers, timer => Assert.Null(timer.At));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact(Timeout = 20000)]
    public async Task OrdinaryCommandOriginsRemainAllowedWithoutConsumingAnAlreadySucceededWindowOrigin()
    {
        var path = DirectoryPath(); var time = Clock(); var date = Date(time);
        try
        {
            using var store = new SqliteAutomationTaskStore(Path.Combine(path, "tasks.db")); var window = await store.SaveAsync(Rule(), default);
            var ordinary = await store.SaveAsync(Rule() with { Detection = new("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", "trigger.exe")) }, default);
            await using var provider = new ProviderFixture(); using var activity = new AutomationFixture(); await using var runtime = Runtime(store, provider, activity, time);
            await runtime.InitializeAsync(); await provider.NextCommand(); await State(store, window, date, "succeeded", 1);
            var scope = ResourceScope.Session(provider.Sid, System.Diagnostics.Process.GetCurrentProcess().SessionId);
            var action = new ProgramTaskAction(true).Compile(ordinary.Action, scope);
            var source = new ResourceKey(WindowOnceCapabilities.Due, WindowOnceCapabilities.Resource(window.Id, WindowOnceCapabilities.Validate(window.Detection)), scope);
            var outcome = await runtime.ExecuteCommandAsync(new(Guid.NewGuid(), action.Key, action.Value, provider.Sid, DateTimeOffset.UtcNow.AddSeconds(30), false,
                [new(window.Id, window.Version, StateValue.From(new("axiom.event-count", 1), (decimal)date.DayNumber * 1024 + 1), source), new(ordinary.Id, ordinary.Version)]), default);
            Assert.Equal(OutcomeStatus.Succeeded, outcome.Status); Assert.Equal(2, provider.Calls); await State(store, window, date, "succeeded", 1);
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact(Timeout = 20000)]
    public async Task DelayedOldWindowCannotBecomeNextDaysAttempt()
    {
        var path = DirectoryPath(); var time = Clock(); var date = Date(time);
        try
        {
            using var store = new SqliteAutomationTaskStore(Path.Combine(path, "tasks.db")); var task = await store.SaveAsync(Rule(delay: "1"), default);
            await using var provider = new ProviderFixture(); using var activity = new AutomationFixture(); await using var runtime = Runtime(store, provider, activity, time);
            await runtime.InitializeAsync(); await Until(async () => (await runtime.ReadAsync(default)).Observations.Any(o => o.Value.Contains("已检测到", StringComparison.Ordinal)));
            time.JumpUtc(TimeSpan.FromDays(1)); await Task.Delay(1200); Assert.Equal(0, provider.Calls);
            Assert.Null(await store.ReadWindowOnceAttemptAsync(task.Id, date)); Assert.Null(await store.ReadWindowOnceAttemptAsync(task.Id, date.AddDays(1)));
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(path, true); }
    }

    private sealed class ProviderFixture : IAsyncDisposable
    {
        public string Sid { get; } = "window-test-" + Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource cancel = new();
        private readonly List<NamedPipeServerStream> streams = [];
        private readonly List<Task> handlers = [];
        private readonly Queue<OutcomeStatus?> results;
        private readonly Channel<ProviderMessage> commands = Channel.CreateUnbounded<ProviderMessage>();
        private readonly Task server;
        private readonly SemaphoreSlim observationWrite = new(1, 1);
        private NamedPipeServerStream? observationPipe;
        private ProviderMessage? observationRequest;
        private long observationSequence;
        private int calls;
        public bool PauseAcceptingAfterFirstConnection;
        public TaskCompletionSource AcceptAgain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref calls);
        public ProviderFixture(params OutcomeStatus?[] results) { this.results = new(results); server = Serve(); }
        public Task<ProviderMessage> NextCommand() => commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task SetWindowQualityAsync(bool known)
        {
            await observationWrite.WaitAsync(cancel.Token);
            try
            {
                var request = observationRequest ?? throw new InvalidOperationException("No observation stream.");
                foreach (var resource in request.Resources!.Where(r => r.CapabilityId == WindowOnceCapabilities.Due))
                    await ProviderProtocol.WriteAsync(observationPipe!, new(1, "update", request.RequestId, Sequence: observationSequence++,
                        Samples: [new(resource, known ? StateQuality.Known : StateQuality.Unknown, "axiom.event-count", 1,
                            Number: known ? 2m : null, Reason: known ? null : "synthetic unavailable")]), cancel.Token);
            }
            finally { observationWrite.Release(); }
        }
        private async Task Serve()
        {
            while (!cancel.IsCancellationRequested)
            {
                var pipe = LocalPipeFactory.Create(ProviderProtocol.PipeName(Sid), WindowsIdentity.GetCurrent().User!.Value);
                try { await pipe.WaitForConnectionAsync(cancel.Token); }
                catch (OperationCanceledException) { pipe.Dispose(); break; }
                handlers.Add(Handle(pipe));
                if (PauseAcceptingAfterFirstConnection && handlers.Count == 1)
                    try { await AcceptAgain.Task.WaitAsync(cancel.Token); } catch (OperationCanceledException) { break; }
            }
            await Task.WhenAll(handlers);
        }
        private async Task Handle(NamedPipeServerStream pipe)
        {
            using (pipe)
            try
            {
                var request = await ProviderProtocol.ReadAsync(pipe, cancel.Token); ProviderProtocol.Validate(request, DateTimeOffset.UtcNow);
                if (request.Type == "observe")
                {
                    lock (streams) streams.Add(pipe);
                    var resources = request.Resources!;
                    await ProviderProtocol.WriteAsync(pipe, new(1, "snapshot", request.RequestId, Sequence: 0,
                        Samples: [.. resources.Select(r => r.CapabilityId == WindowOnceCapabilities.Due
                            ? new ProviderSample(r, StateQuality.Known, "axiom.event-count", 1, Number: 0)
                            : new ProviderSample(r, StateQuality.Known, "axiom.bool", 1, Boolean: false))]), cancel.Token);
                    long sequence = 1;
                    foreach (var resource in resources.Where(r => r.CapabilityId == WindowOnceCapabilities.Due))
                        await ProviderProtocol.WriteAsync(pipe, new(1, "update", request.RequestId, Sequence: sequence++, Samples: [new(resource, StateQuality.Known, "axiom.event-count", 1, Number: 1)]), cancel.Token);
                    await observationWrite.WaitAsync(cancel.Token);
                    try { observationPipe = pipe; observationRequest = request; observationSequence = sequence; }
                    finally { observationWrite.Release(); }
                    await ProviderProtocol.ReadAsync(pipe, cancel.Token);
                }
                else
                {
                    Interlocked.Increment(ref calls); commands.Writer.TryWrite(request);
                    OutcomeStatus? status; lock (results) status = results.Count > 0 ? results.Dequeue() : OutcomeStatus.Succeeded;
                    if (status is { } result) await ProviderProtocol.WriteAsync(pipe, new(1, "result", request.RequestId, CommandId: request.CommandId, Status: result, Reason: "synthetic action receipt"), cancel.Token);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            cancel.Cancel(); lock (streams) foreach (var pipe in streams) pipe.Dispose();
            await server; cancel.Dispose();
        }
    }
}
