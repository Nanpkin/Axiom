using System.Collections.Immutable;
using System.IO.Pipes;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.ServiceHost;

namespace Axiom.Engine.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsKeepAwakeRuntimeTests
{
    [Theory(Timeout = 15000)]
    [InlineData("false")]
    [InlineData("unknown")]
    [InlineData("pause")]
    [InlineData("delete")]
    [InlineData("observeDisconnect")]
    public async Task HostLeaseReleasesAfterConditionOrConfigurationEndsAndSharesAcrossRules(string end)
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        var first = fixture.Tasks[0]; var second = fixture.Tasks[1];
        using var lease = fixture.Pipe(); var accepted = lease.WaitForConnectionAsync(fixture.Token);
        await fixture.UpdateAsync("process.running", true); await accepted;
        var request = await ProviderProtocol.ReadAsync(lease, fixture.Token); Assert.Equal("lease", request.Type);
        await ProviderProtocol.WriteAsync(lease, new(1, "result", request.RequestId, Status: OutcomeStatus.Succeeded, Reason: "synthetic owned request"), fixture.Token);
        await fixture.UpdateAsync(KeepAwakeCapabilities.Hold, true);
        await fixture.WaitStateAsync("已保持唤醒");
        var disconnected = ProviderProtocol.ReadAsync(lease, fixture.Token);
        // Removing one of two identical winners keeps observation resources and the request alive.
        await fixture.Runtime.SaveAsync(first with { Paused = true }, fixture.Token);
        Assert.False(disconnected.IsCompleted);
        switch (end)
        {
            case "false": await fixture.UpdateAsync("process.running", false); break;
            case "unknown": await fixture.UpdateAsync("process.running", null); break;
            case "pause": await fixture.Runtime.SaveAsync(second with { Paused = true }, fixture.Token); break;
            case "delete": await fixture.Runtime.DeleteAsync(second.Id, second.Version, fixture.Token); break;
            case "observeDisconnect": fixture.Observation.Dispose(); break;
        }
        await Assert.ThrowsAnyAsync<IOException>(() => disconnected.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact(Timeout = 15000)]
    public async Task LosingOnlyLeaseDoesNotReacquireUntilNewConditionAndOldCleanupCannotCloseReplacement()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        using var lease = fixture.Pipe(); var accepted = lease.WaitForConnectionAsync(fixture.Token);
        await fixture.UpdateAsync("process.running", true); await accepted;
        var request = await ProviderProtocol.ReadAsync(lease, fixture.Token);
        await ProviderProtocol.WriteAsync(lease, new(1, "result", request.RequestId, Status: OutcomeStatus.Succeeded, Reason: "synthetic owned request"), fixture.Token);
        await fixture.UpdateAsync(KeepAwakeCapabilities.Hold, true); await fixture.WaitStateAsync("已保持唤醒");
        lease.Dispose(); await fixture.WaitStateAsync("保持唤醒连接中断");
        using var replacement = fixture.Pipe(); var reconnected = replacement.WaitForConnectionAsync(fixture.Token);
        // Observation remains connected, including the Agent's false ownership update.
        await fixture.UpdateAsync(KeepAwakeCapabilities.Hold, false);
        await Task.Delay(200, fixture.Token); Assert.False(reconnected.IsCompleted);
        await fixture.UpdateAsync(KeepAwakeCapabilities.Hold, false);
        await Task.Delay(200, fixture.Token); Assert.False(reconnected.IsCompleted);
        await fixture.UpdateAsync("process.running", false); await fixture.UpdateAsync("process.running", true);
        await reconnected.WaitAsync(TimeSpan.FromSeconds(2));
        var fresh = await ProviderProtocol.ReadAsync(replacement, fixture.Token); Assert.Equal("lease", fresh.Type);
        await ProviderProtocol.WriteAsync(replacement, new(1, "result", fresh.RequestId, Status: OutcomeStatus.Succeeded, Reason: "synthetic replacement"), fixture.Token);
        await fixture.UpdateAsync(KeepAwakeCapabilities.Hold, true); await fixture.WaitStateAsync("已保持唤醒");
        var stillHeld = ProviderProtocol.ReadAsync(replacement, fixture.Token);
        await fixture.Runtime.EnsureSafeToStopAsync(fixture.Token);
        await Assert.ThrowsAnyAsync<IOException>(() => stillHeld.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact(Timeout = 15000)]
    public async Task ReceiptImmediatelyFollowedByDisconnectDoesNotDeadlockAgainstSnapshotProcessing()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        using var lease = fixture.Pipe(); var accepted = lease.WaitForConnectionAsync(fixture.Token);
        await fixture.UpdateAsync("process.running", true); await accepted;
        var request = await ProviderProtocol.ReadAsync(lease, fixture.Token);
        await ProviderProtocol.WriteAsync(lease, new(1, "result", request.RequestId, Status: OutcomeStatus.Succeeded, Reason: "synthetic short-lived lease"), fixture.Token);
        lease.Dispose();
        await fixture.SendSnapshotAsync(true, false);
        await fixture.WaitStateAsync("保持唤醒连接中断");
        await fixture.Runtime.EnsureSafeToStopAsync(fixture.Token).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHandshakeDoesNotRetryAfterFreshOwnershipSamplesUntilConditionRestarts(bool rejected)
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        using var lease = fixture.Pipe(); var accepted = lease.WaitForConnectionAsync(fixture.Token);
        await fixture.UpdateAsync("process.running", true); await accepted;
        var request = await ProviderProtocol.ReadAsync(lease, fixture.Token);
        if (rejected)
        {
            await ProviderProtocol.WriteAsync(lease, new(1, "result", request.RequestId, Status: OutcomeStatus.Failed, Reason: "synthetic rejected power request"), fixture.Token);
            await Assert.ThrowsAnyAsync<IOException>(() => ProviderProtocol.ReadAsync(lease, fixture.Token).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        else { lease.Dispose(); await fixture.WaitStateAsync("保持唤醒未完成"); }
        using var replacement = fixture.Pipe(); var reconnected = replacement.WaitForConnectionAsync(fixture.Token);
        await fixture.UpdateAsync(KeepAwakeCapabilities.Hold, true); await fixture.UpdateAsync(KeepAwakeCapabilities.Hold, false);
        await Task.Delay(200, fixture.Token); Assert.False(reconnected.IsCompleted);
        await fixture.UpdateAsync(KeepAwakeCapabilities.Hold, false);
        await Task.Delay(200, fixture.Token); Assert.False(reconnected.IsCompleted);
        await fixture.UpdateAsync("process.running", false); await fixture.UpdateAsync("process.running", true);
        await reconnected.WaitAsync(TimeSpan.FromSeconds(2));
        var fresh = await ProviderProtocol.ReadAsync(replacement, fixture.Token);
        await ProviderProtocol.WriteAsync(replacement, new(1, "result", fresh.RequestId, Status: OutcomeStatus.Succeeded, Reason: "synthetic renewed request"), fixture.Token);
        await fixture.UpdateAsync(KeepAwakeCapabilities.Hold, true); await fixture.WaitStateAsync("已保持唤醒");
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string sid = "synthetic-power-" + Guid.NewGuid().ToString("N");
        private readonly string userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "axiom-power-runtime-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        private readonly AutomationFixture activity = new();
        private SqliteAutomationTaskStore store = null!;
        public PluginTaskRuntime Runtime = null!;
        public NamedPipeServerStream Observation = null!;
        public AutomationTask[] Tasks = [];
        private ProviderMessage request = null!;
        private long sequence;
        public CancellationToken Token => timeout.Token;
        public NamedPipeServerStream Pipe() => LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), userSid, 8);
        public static async Task<RuntimeFixture> StartAsync()
        {
            var fixture = new RuntimeFixture(); Directory.CreateDirectory(fixture.directory);
            fixture.store = new(Path.Combine(fixture.directory, "tasks.db"));
            var detection = new TaskInvocation("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", "synthetic-transfer.exe"));
            var action = new TaskInvocation(KeepAwakeCapabilities.Hold, 1, ImmutableDictionary<string, string>.Empty.Add("target", "system"));
            fixture.Tasks = [await fixture.store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "synthetic first", false, detection, action), fixture.Token),
                await fixture.store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "synthetic second", false, detection, action), fixture.Token)];
            fixture.Observation = fixture.Pipe(); var accepted = fixture.Observation.WaitForConnectionAsync(fixture.Token);
            fixture.Runtime = new([new ProgramPlugin(), new KeepAwakePlugin()], fixture.store, fixture.sid, fixture.activity.Engine);
            await fixture.Runtime.InitializeAsync(); await accepted;
            fixture.request = await ProviderProtocol.ReadAsync(fixture.Observation, fixture.Token);
            Assert.Contains(new ProviderResource(KeepAwakeCapabilities.Hold, "system"), fixture.request.Resources!);
            await fixture.SendSnapshotAsync(false, false);
            for (var i = 0; i < 100 && !(await fixture.Runtime.ReadAsync(fixture.Token)).DetectionAvailable; i++) await Task.Delay(10, fixture.Token);
            Assert.True((await fixture.Runtime.ReadAsync(fixture.Token)).DetectionAvailable);
            return fixture;
        }
        public Task SendSnapshotAsync(bool running, bool owned) => ProviderProtocol.WriteAsync(Observation,
            new(1, "snapshot", request.RequestId, Sequence: sequence++, Samples: [.. request.Resources!.Select(r =>
                new ProviderSample(r, StateQuality.Known, "axiom.bool", 1, Boolean: r.CapabilityId == KeepAwakeCapabilities.Hold ? owned : running))]), Token);
        public Task UpdateAsync(string capability, bool? value) => ProviderProtocol.WriteAsync(Observation,
            new(1, "update", request.RequestId, Sequence: sequence++, Samples: [new(request.Resources!.Single(r => r.CapabilityId == capability),
                value.HasValue ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1, Boolean: value)]), Token);
        public async Task WaitStateAsync(string state)
        {
            for (var i = 0; i < 200 && (await Runtime.ReadAsync(Token)).State != state; i++) await Task.Delay(10, Token);
            Assert.Equal(state, (await Runtime.ReadAsync(Token)).State);
        }
        public async ValueTask DisposeAsync()
        {
            if (Runtime is not null) { await Runtime.EnsureSafeToStopAsync(Token); await Runtime.DisposeAsync(); }
            Observation?.Dispose(); store?.Dispose(); activity.Dispose(); timeout.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
