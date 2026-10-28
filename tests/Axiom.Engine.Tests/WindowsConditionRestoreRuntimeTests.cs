using System.Collections.Immutable;
using System.IO.Pipes;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.ServiceHost;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

// Windows pipe ownership + actual Host/Agent lease stream; the setting itself is entirely synthetic.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsConditionRestoreRuntimeTests
{
    [Theory(Timeout = 15000)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task CanceledInitialReplyIsDrainedAndNextGenerationWaitsForConfirmedRelease(bool partialReply, bool endCondition, bool earlyNext, bool releaseSucceeded)
    {
        var sid = "synthetic-handoff-" + Guid.NewGuid().ToString("N");
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var directory = Path.Combine(Path.GetTempPath(), "axiom-handoff-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var canceled = new CancellationTokenSource();
        using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
        await using var runtime = new PluginTaskRuntime([new SyntheticHardwarePlugin()], store, sid, activity.Engine);
        var key = new ResourceKey(HardwareBrightnessRestoreCapabilities.Set, "brightness", ResourceScope.Session(sid, System.Diagnostics.Process.GetCurrentProcess().SessionId));
        var invocation = new TaskInvocation(key.CapabilityId, 1, ImmutableDictionary<string, string>.Empty.Add("target", "primary").Add("percent", "20").Add("delaySeconds", "0"));
        DesiredState Desired(long generation) => new(key, StateValue.From(RestorableSettingInvocations.Schema, RestorableSettingInvocations.Encode(invocation)), "synthetic", 0, generation, "condition ended");
        using var first = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), user, 8);
        using var second = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), user, 8);
        var initialEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAccepted = first.WaitForConnectionAsync(stop.Token); var secondAccepted = second.WaitForConnectionAsync(stop.Token);
        var released = false;
        async Task ServeFirst()
        {
            await firstAccepted;
            var request = await ProviderProtocol.ReadAsync(first, stop.Token);
            using var frame = new MemoryStream(); await Reply(frame, request);
            var bytes = frame.ToArray();
            if (partialReply) await first.WriteAsync(bytes.AsMemory(0, 8), stop.Token);
            initialEntered.TrySetResult(); await initialReply.Task.WaitAsync(stop.Token);
            await first.WriteAsync(bytes.AsMemory(partialReply ? 8 : 0), stop.Token);
            var release = await ProviderProtocol.ReadAsync(first, stop.Token);
            if (!endCondition)
            {
                Assert.Equal("lease-setting-update", release.Type); await Reply(first, release);
                release = await ProviderProtocol.ReadAsync(first, stop.Token);
            }
            Assert.Equal("lease-setting-release", release.Type); Assert.Equal(endCondition ? 1 : 2, release.Sequence);
            releaseEntered.TrySetResult(); await finishRelease.Task.WaitAsync(stop.Token);
            released = true; await Reply(first, release, releaseSucceeded);
        }
        async Task ServeSecond()
        {
            await secondAccepted;
            var request = await ProviderProtocol.ReadAsync(second, stop.Token);
            Assert.True(released, "A new baseline must not be captured before the previous release.");
            await Reply(second, request);
            var release = await ProviderProtocol.ReadAsync(second, stop.Token);
            Assert.Equal("lease-setting-release", release.Type); await Reply(second, release);
        }
        Task Reply(Stream pipe, ProviderMessage message, bool success = true) => ProviderProtocol.WriteAsync(pipe,
            new(1, "lease-setting-result", message.RequestId, Sequence: message.Sequence, Status: success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, Reason: "synthetic confirmed"), stop.Token);
        var servingFirst = ServeFirst(); var servingSecond = ServeSecond();
        try
        {
            runtime.SetDesiredAuthorization(key, 1, true);
            var applyingFirst = runtime.ApplyDesiredAsync(Desired(1), canceled.Token).AsTask();
            await initialEntered.Task.WaitAsync(stop.Token);
            if (endCondition) runtime.SetDesiredAuthorization(key, 2, false);
            canceled.Cancel();
            Task<Outcome>? applyingSecond = null;
            if (earlyNext)
            {
                runtime.SetDesiredAuthorization(key, 3, true);
                applyingSecond = runtime.ApplyDesiredAsync(Desired(3), stop.Token).AsTask();
            }
            initialReply.TrySetResult();
            if (!endCondition)
            {
                Assert.NotEqual(OutcomeStatus.Succeeded, (await applyingFirst).Status);
                Assert.Equal(OutcomeStatus.Succeeded, (await applyingSecond!).Status);
                Assert.False(secondAccepted.IsCompleted);
                runtime.SetDesiredAuthorization(key, 4, false);
                finishRelease.TrySetResult(); await servingFirst;
                await runtime.EnsureSafeToStopAsync(stop.Token);
                return;
            }
            // Await either the requested release or the pipe server's concrete framing failure.
            await Task.WhenAny(releaseEntered.Task, servingFirst).WaitAsync(stop.Token);
            if (servingFirst.IsCompleted) await servingFirst;
            Assert.True(releaseEntered.Task.IsCompleted);
            if (!earlyNext)
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                new Thread(() =>
                {
                    try { runtime.SetDesiredAuthorization(key, 3, true); started.TrySetResult(); }
                    catch (Exception error) { started.TrySetException(error); }
                }) { IsBackground = true }.Start();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                applyingSecond = runtime.ApplyDesiredAsync(Desired(3), stop.Token).AsTask();
            }
            Assert.False(secondAccepted.IsCompleted);
            finishRelease.TrySetResult(); await servingFirst;
            Assert.NotEqual(OutcomeStatus.Succeeded, (await applyingFirst).Status);
            if (!releaseSucceeded)
            {
                Assert.Equal(OutcomeStatus.Failed, (await applyingSecond!).Status);
                for (var generation = 4; generation < 8; generation++)
                {
                    runtime.SetDesiredAuthorization(key, generation, true);
                    Assert.Equal(OutcomeStatus.Failed, (await runtime.ApplyDesiredAsync(Desired(generation), stop.Token)).Status);
                    Assert.False(secondAccepted.IsCompleted, "Failed restoration must never be replaced by a fresh baseline.");
                }
                return;
            }
            Assert.Equal(OutcomeStatus.Succeeded, (await applyingSecond!).Status);
            runtime.SetDesiredAuthorization(key, 4, false);
            await servingSecond;
            await runtime.EnsureSafeToStopAsync(stop.Token);
            Assert.Equal("条件设置已释放", (await runtime.ReadAsync(stop.Token)).State);
        }
        finally
        {
            initialReply.TrySetResult(); finishRelease.TrySetResult();
            stop.Cancel(); first.Dispose(); second.Dispose();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await runtime.EnsureSafeToStopAsync(cleanup.Token);
            try { await Task.WhenAll(servingFirst, servingSecond); } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            store.Dispose(); Directory.Delete(directory, true);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task HostSharesLeaseUpdatesWinnerWithoutRecapturingAndStopReportsActualOriginalBaseline()
    {
        var sid = "synthetic-restore-" + Guid.NewGuid().ToString("N");
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var directory = Path.Combine(Path.GetTempPath(), "axiom-synthetic-restore-runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
        NamedPipeServerStream Pipe() => LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), user, 8);
        var native = new SyntheticState(); var captures = 0;
        var provider = new RestorableSettingCapabilityProvider(HardwareBrightnessCapabilities.Set + ".restore",
            (_, _) => { Interlocked.Increment(ref captures); return Task.FromResult<IRestorableSettingSession>(new SyntheticSetting(native)); },
            SyntheticHardwarePlugin.Validate, _ => "brightness");
        var target = ""; var ownership = Channel.CreateBounded<ProviderSample>(32);
        var observing = provider.ObserveAsync([new(HardwareBrightnessCapabilities.Set + ".restore", "brightness")], update =>
        { foreach (var sample in update.Samples) { Volatile.Write(ref target, sample.Text!); Assert.True(ownership.Writer.TryWrite(sample)); } }, stop.Token);
        TaskInvocation Action(int value, string selection = "primary") => new(HardwareBrightnessCapabilities.Set + ".restore", 1, ImmutableDictionary<string, string>.Empty
            .Add("target", selection).Add("percent", value.ToString()).Add("delaySeconds", "0"));
        var detection = new TaskInvocation("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", "synthetic.exe"));
        var first = await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "synthetic first", false, detection, Action(20)), stop.Token);
        var second = await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "synthetic second", false, detection, Action(20)), stop.Token);
        using var observation = Pipe(); var accepted = observation.WaitForConnectionAsync(stop.Token);
        await using var runtime = new PluginTaskRuntime([new ProgramPlugin(), new SyntheticHardwarePlugin()], store, sid, activity.Engine);
        Task? sending = null, leaseStream = null, replacementStream = null; NamedPipeServerStream? leasePipe = null, replacementPipe = null;
        try
        {
            await runtime.InitializeAsync(); await accepted;
            var request = await ProviderProtocol.ReadAsync(observation, stop.Token); long sequence = 0;
            var input = request.Resources!.Single(r => r.CapabilityId == "process.running");
            await ProviderProtocol.WriteAsync(observation, new(1, "snapshot", request.RequestId, Sequence: sequence++, Samples:
                [new(input, StateQuality.Known, "axiom.bool", 1, Boolean: false),
                 new(new(HardwareBrightnessCapabilities.Set + ".restore", "brightness"), StateQuality.Known, RestorableSettingInvocations.Schema.Id, 1, Text: target)]), stop.Token);
            while (ownership.Reader.TryRead(out _)) { }
            var writes = new SemaphoreSlim(1, 1);
            async Task Send(ProviderSample sample)
            {
                await writes.WaitAsync(stop.Token);
                try { await ProviderProtocol.WriteAsync(observation, new(1, "update", request.RequestId, Sequence: sequence++, Samples: [sample]), stop.Token); }
                finally { writes.Release(); }
            }
            sending = Task.Run(async () =>
            {
                try { await foreach (var sample in ownership.Reader.ReadAllAsync(stop.Token)) await Send(sample); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            });
            await Wait(async () => (await runtime.ReadAsync(stop.Token)).DetectionAvailable);
            leasePipe = Pipe(); var leaseAccepted = leasePipe.WaitForConnectionAsync(stop.Token);
            await Send(new(input, StateQuality.Known, "axiom.bool", 1, Boolean: true)); await leaseAccepted;
            var leaseRequest = await ProviderProtocol.ReadAsync(leasePipe, stop.Token); Assert.Equal("lease-setting", leaseRequest.Type);
            leaseStream = SessionRestorableLeaseStream.RunAsync(leasePipe, leaseRequest, provider, stop.Token);
            await Wait(async () => (await runtime.ReadAsync(stop.Token)).Reason == "synthetic applied=20");
            Assert.Equal(1, captures); Assert.Equal(20, native.Value);
            await runtime.SaveAsync(first with { Paused = true }, stop.Token);
            Assert.False(leaseStream.IsCompleted); Assert.Equal(1, captures);
            await runtime.SaveAsync(second with { Action = Action(50) }, stop.Token);
            await Wait(async () => (await runtime.ReadAsync(stop.Token)).Reason == "synthetic applied=50");
            Assert.Equal(1, captures); Assert.Equal(50, native.Value);
            replacementPipe = Pipe(); var replacementAccepted = replacementPipe.WaitForConnectionAsync(stop.Token);
            replacementStream = Replacement();
            var current = (await runtime.ReadAsync(stop.Token)).Tasks.Single(t => t.Id == second.Id);
            await runtime.SaveAsync(current with { Action = Action(40, "all") }, stop.Token);
            await Wait(async () => (await runtime.ReadAsync(stop.Token)).Reason == "synthetic applied=40");
            Assert.Equal(2, captures); Assert.Equal(1, native.Disposed); Assert.Equal(40, native.Value);
            await runtime.EnsureSafeToStopAsync(stop.Token);
            await leaseStream.WaitAsync(TimeSpan.FromSeconds(2));
            await replacementStream.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(75, native.Value); Assert.Equal(2, native.Disposed);
            var released = await runtime.ReadAsync(stop.Token);
            Assert.Equal("条件设置已释放", released.State); Assert.Contains("最初", released.Reason);

            async Task Wait(Func<Task<bool>> predicate)
            {
                for (var i = 0; i < 200; i++) { if (await predicate()) return; await Task.Delay(10, stop.Token); }
                Assert.True(await predicate());
            }
            async Task Replacement()
            {
                await replacementAccepted;
                var replacementRequest = await ProviderProtocol.ReadAsync(replacementPipe, stop.Token);
                await SessionRestorableLeaseStream.RunAsync(replacementPipe, replacementRequest, provider, stop.Token);
            }
        }
        finally
        {
            await runtime.EnsureSafeToStopAsync(stop.Token);
            stop.Cancel(); leasePipe?.Dispose(); replacementPipe?.Dispose(); observation.Dispose();
            try { await observing; } catch (OperationCanceledException) { }
            if (sending is not null) await sending;
            if (leaseStream is not null) await leaseStream;
            if (replacementStream is not null) await replacementStream;
            store.Dispose(); Directory.Delete(directory, true);
        }
    }
    private sealed class SyntheticHardwarePlugin : IAutomationPlugin
    {
        private const string Capability = HardwareBrightnessCapabilities.Set + ".restore";
        public string Id => "synthetic.hardware.restore";
        public IEnumerable<ITaskDetectionCompiler> Detections => [];
        public IEnumerable<ITaskActionCompiler> Actions => [new RestorableSettingTaskAction(HardwareBrightnessCapabilities.Descriptor with { Id = Capability }, Validate, _ => "brightness")];
        public ImmutableArray<TaskCombination> Combinations => [new("process.running", Capability)];
        public IEnumerable<CapabilityBinding> Bindings => [RestorableSettingTaskAction.Binding(Capability)];
        public static void Validate(TaskInvocation invocation) => HardwareBrightnessCapabilities.Validate(invocation with { CapabilityId = HardwareBrightnessCapabilities.Set });
    }
    private sealed class SyntheticState { public int Value = 75, Disposed; }
    private sealed class SyntheticSetting : IRestorableSettingSession
    {
        private readonly SyntheticState state;
        private readonly ConditionalSettingTransaction<int> transaction;
        public SyntheticSetting(SyntheticState state) { this.state = state; transaction = new(state.Value, () => state.Value, value => state.Value = value, "合成设置"); }
        public Task<RestorableSettingResult> ApplyAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken token)
        {
            var result = transaction.Apply(int.Parse(parameters["percent"]), token);
            return Task.FromResult(result with { Reason = "synthetic applied=" + state.Value });
        }
        public Task<RestorableSettingResult> ReleaseAsync(CancellationToken token) => Task.FromResult(transaction.Release(token));
        public ValueTask DisposeAsync() { state.Disposed++; return ValueTask.CompletedTask; }
    }
}
