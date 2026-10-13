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
