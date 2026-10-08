using System.Collections.Immutable;
using System.IO.Pipes;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.ServiceHost;

namespace Axiom.Engine.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PluginRuntimeTests
{
    [Fact(Timeout = 10000)]
    public async Task UnavailableHotkeyDoesNotDisconnectOrBlockUnrelatedProgramRule()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "axiom-hotkey-gap-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var token = timeout.Token;
            var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            using var observation = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), userSid); var accepted = observation.WaitForConnectionAsync(token);
            using var fixture = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            var action = new TaskInvocation("program.start", 1, ProgramCapabilities.Describe(true).Fields.Where(f => f.DefaultValue is not null)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).Add("executable", @"C:\Apps\target.exe"));
            await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "occupied hotkey", false,
                new(HotkeyCapabilities.Pressed, 1, ImmutableDictionary<string,string>.Empty.Add("chords", "Ctrl+Alt+K")), action), token);
            await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "process", false,
                new("process.running", 2, ImmutableDictionary<string,string>.Empty.Add("processNames", "trigger.exe")), action), token);
            await using var runtime = new PluginTaskRuntime([new ProgramPlugin(), new KeyboardPlugin()], store, sid, fixture.Engine);
            await runtime.InitializeAsync(); await accepted.WaitAsync(TimeSpan.FromSeconds(2));
            var request = await ProviderProtocol.ReadAsync(observation, token);
            var process = request.Resources!.Single(r => r.CapabilityId == "process.running"); var hotkey = request.Resources!.Single(r => r.CapabilityId == HotkeyCapabilities.Pressed);
            await ProviderProtocol.WriteAsync(observation, new(1, "snapshot", request.RequestId, Samples: [
                new(process, StateQuality.Known, "axiom.bool", 1, Boolean: false),
                new(hotkey, StateQuality.Unknown, "axiom.event-count", 1, Reason: "快捷键被占用")]), token);
            for (var i = 0; i < 100 && !(await runtime.ReadAsync(token)).DetectionAvailable; i++) await Task.Delay(10, token);
            var snapshot = await runtime.ReadAsync(token); Assert.True(snapshot.DetectionAvailable);
            Assert.Contains(snapshot.Observations, o => o.Value == "快捷键被占用");
            using var invoked = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), userSid); var called = invoked.WaitForConnectionAsync(token);
            await ProviderProtocol.WriteAsync(observation, new(1, "update", request.RequestId, Sequence: 1,
                Samples: [new(process, StateQuality.Known, "axiom.bool", 1, Boolean: true)]), token);
            await called.WaitAsync(TimeSpan.FromSeconds(2)); var command = await ProviderProtocol.ReadAsync(invoked, token);
            Assert.Equal("program.start", command.Invocation!.CapabilityId);
            await ProviderProtocol.WriteAsync(invoked, new(1, "result", command.RequestId, CommandId: command.CommandId, Status: OutcomeStatus.Succeeded, Reason: "unrelated action ran"), token);
            await runtime.EnsureSafeToStopAsync(token);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact(Timeout = 30000)]
    public async Task AllSupportedWaitingRulesLeaveRoomForLastWindowRuleAndPauseCancelsWaitingCall()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "axiom-wait-runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var token = deadline.Token;
            var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            NamedPipeServerStream Pipe() => LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), userSid, AutomationTaskLimits.MaximumTasks + 2);
            using var observation = Pipe();
            var accepted = observation.WaitForConnectionAsync(token);
            using var fixture = new AutomationFixture();
            using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            TaskInvocation Detection(string name) => new("process.running", 2, ImmutableDictionary<string,string>.Empty.Add("processNames", name));
            TaskInvocation Action(TaskCapability descriptor, string path) => new(descriptor.Id, descriptor.SchemaVersion,
                descriptor.Fields.Where(f => f.DefaultValue is not null).ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).Add("executable", path));
            var waitingTasks = new List<AutomationTask>();
            for (var i = 0; i < AutomationTaskLimits.MaximumTasks - 1; i++)
                waitingTasks.Add(await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, $"wait for tray {i}", false, Detection($"wait{i}.exe"),
                    Action(TrayCapabilities.Descriptor, $@"C:\Apps\missing-tray{i}.exe")), token));
            await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "close window", false, Detection("close.exe"),
                Action(WindowCapabilities.Descriptor, @"C:\Apps\headset.exe")), token);
            await using var runtime = new PluginTaskRuntime([new ProgramPlugin(), new TrayPlugin(), new WindowPlugin()], store, sid, fixture.Engine);
            await runtime.InitializeAsync(); await accepted.WaitAsync(TimeSpan.FromSeconds(2));
            var request = await ProviderProtocol.ReadAsync(observation, token);
            ProviderSample Sample(string name, bool value) => new(request.Resources!.Single(r => r.ResourceId == name), StateQuality.Known, "axiom.bool", 1, Boolean: value);
            await ProviderProtocol.WriteAsync(observation, new(1, "snapshot", request.RequestId,
                Samples: [.. Enumerable.Range(0, waitingTasks.Count).Select(i => Sample($"wait{i}.exe", false)), Sample("close.exe", false)]), token);
            for (var i=0; i<100 && !(await runtime.ReadAsync(token)).DetectionAvailable; i++) await Task.Delay(10, token);
            Assert.True((await runtime.ReadAsync(token)).DetectionAvailable);
            var waitPipes = new List<NamedPipeServerStream>();
            try
            {
                for (var i = 0; i < waitingTasks.Count; i++)
                {
                    var pipe = Pipe(); waitPipes.Add(pipe);
                    var called = pipe.WaitForConnectionAsync(token);
                    await ProviderProtocol.WriteAsync(observation, new(1, "update", request.RequestId, Sequence: i + 1,
                        Samples: [Sample($"wait{i}.exe", true)]), token);
                    await called.WaitAsync(TimeSpan.FromSeconds(2));
                    var tray = await ProviderProtocol.ReadAsync(pipe, token);
                    Assert.Equal(TrayCapabilities.Hide, tray.Invocation!.CapabilityId);
                    Assert.Equal($@"C:\Apps\missing-tray{i}.exe", tray.Invocation.Parameters["executable"]);
                }
                // All 127 commands remain genuinely in flight without receipts; even the last supported rule can run.
                using var windowPipe = Pipe();
                var windowCall = windowPipe.WaitForConnectionAsync(token);
                await ProviderProtocol.WriteAsync(observation, new(1, "update", request.RequestId, Sequence: waitingTasks.Count + 1, Samples: [Sample("close.exe", true)]), token);
                await windowCall.WaitAsync(TimeSpan.FromSeconds(2));
                var window = await ProviderProtocol.ReadAsync(windowPipe, token);
                Assert.Equal(WindowCapabilities.Close, window.Invocation!.CapabilityId);
                Assert.Equal(@"C:\Apps\headset.exe", window.Invocation.Parameters["executable"]);
                await ProviderProtocol.WriteAsync(windowPipe, new(1, "result", window.RequestId, CommandId: window.CommandId, Status: OutcomeStatus.Succeeded, Reason: "window closed while tray waits"), token);
                var waiting = waitingTasks[0];
                var disconnected = ProviderProtocol.ReadAsync(waitPipes[0], token);
                await runtime.SaveAsync(waiting with { Paused = true }, token);
                await Assert.ThrowsAnyAsync<IOException>(() => disconnected.WaitAsync(TimeSpan.FromSeconds(2)));
                await runtime.EnsureSafeToStopAsync(token);
                Assert.True((await store.ListAsync(token)).Single(t => t.Id == waiting.Id).Paused);
            }
            finally { foreach (var pipe in waitPipes) pipe.Dispose(); }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact(Timeout = 10000)]
    public async Task SharedRuntimeSubscribesOnlyObservationsExecutesCommandAndCanStopAfterLastWatchReleased()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "axiom-runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var cancel = new CancellationTokenSource();
            var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            using var observation = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), userSid);
            var accepted = observation.WaitForConnectionAsync(cancel.Token);
            using var fixture = new AutomationFixture();
            using var store = new SqliteAutomationTaskStore(Path.Combine(directory,"tasks.db"));
            await using var runtime = new PluginTaskRuntime([new ProgramPlugin()],store,sid,fixture.Engine);
            await runtime.InitializeAsync();
            var parameters=ProgramCapabilities.Describe(true).Fields.Where(f=>f.DefaultValue is not null)
                .ToImmutableDictionary(f=>f.Id,f=>f.DefaultValue!).Add("executable", @"C:\Apps\target.exe");
            var task = new AutomationTask(Guid.NewGuid().ToString("D"),0,"integration",false,
                new("process.running",2,ImmutableDictionary<string,string>.Empty.Add("processNames","trigger.exe")),new("program.start",1,parameters));
            var saved = await runtime.SaveAsync(task,default); await accepted.WaitAsync(TimeSpan.FromSeconds(2));
            var request=await ProviderProtocol.ReadAsync(observation,cancel.Token);
            Assert.Equal("observe",request.Type);
            Assert.Equal([new ProviderResource("process.running","trigger.exe")],request.Resources!);
            Assert.Contains("program.start",request.RequiredCapabilities!);
            await ProviderProtocol.WriteAsync(observation,new(1,"snapshot",request.RequestId,Samples:[new(request.Resources![0],StateQuality.Known,"axiom.bool",1,Boolean:false)]),cancel.Token);
            // Snapshot processing is asynchronous; wait on exposed state rather than relying on fixed startup sleep.
            for (var i=0;i<100 && !(await runtime.ReadAsync(default)).DetectionAvailable;i++) await Task.Delay(10);
            Assert.True((await runtime.ReadAsync(default)).DetectionAvailable);
            using var invoke = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), userSid);
            var called=invoke.WaitForConnectionAsync(cancel.Token);
            await ProviderProtocol.WriteAsync(observation,new(1,"update",request.RequestId,Sequence:1,Samples:[new(request.Resources[0],StateQuality.Known,"axiom.bool",1,Boolean:true)]),cancel.Token);
            await called.WaitAsync(TimeSpan.FromSeconds(2)); var command=await ProviderProtocol.ReadAsync(invoke,cancel.Token);
            Assert.Equal("invoke",command.Type);Assert.Equal("program.start",command.Invocation!.CapabilityId);
            await ProviderProtocol.WriteAsync(invoke,new(1,"result",command.RequestId,CommandId:command.CommandId,Status:OutcomeStatus.Succeeded,Reason:"test receipt"),cancel.Token);
            await runtime.SaveAsync(saved with {Paused=true},default);
            Assert.True((await runtime.ReadAsync(default)).DetectionAvailable);
            await runtime.EnsureSafeToStopAsync(default);
            Assert.True((await store.ListAsync()).Single().Paused);
        }
        finally { Directory.Delete(directory,true); }
    }
}
