using System.Collections.Immutable;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.ServiceHost;
namespace Axiom.Engine.Tests;
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class GameModeTests
{
    [Fact(Timeout=10000)]
    public async Task CompleteProductionCompositionInitializesWithUniqueGameModeCombinations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "axiom-game-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var activity = new AutomationFixture();
            using var store = new SqliteAutomationTaskStore(Path.Combine(dir, "tasks.db"));
            await using var runtime = new PluginTaskRuntime(BuiltInAutomationPlugins.Create().Append(new TrayToolPlugin()),
                store, "test-" + Guid.NewGuid().ToString("N"), activity.Engine, () => 42);
            await runtime.InitializeAsync();
            Assert.Single(runtime.Combinations.Where(c => c.DetectionId == GameModeCapabilities.State && c.ActionId == HostsCapabilities.Update));
            Assert.False(runtime.ReadGameMode().Active);
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(dir, true); }
    }
    private static AutomationTask Rule() => new(Guid.NewGuid().ToString("D"),0,"game mode",false,
        new(GameModeCapabilities.State,1,ImmutableDictionary<string,string>.Empty.Add("state","on")),
        new("program.start",1,ProgramCapabilities.Describe(true).Fields.Where(f=>f.DefaultValue is not null).ToImmutableDictionary(f=>f.Id,f=>f.DefaultValue!).SetItem("executable", @"C:\Apps\synthetic.exe")));
    [Fact(Timeout=20000)]
    public async Task ManualBaselineDoesNotReplayAndSubsequentChangesTriggerWithoutUI()
    {
        var dir=Path.Combine(Path.GetTempPath(),"axiom-game-"+Guid.NewGuid().ToString("N"));
        try {
            using var activity=new AutomationFixture(); using var store=new SqliteAutomationTaskStore(Path.Combine(dir,"tasks.db"));
            await store.SaveGameModeSettingsAsync(new(ManualEnabled:true),default);
            var saved=await store.SaveAsync(Rule(),default);
            var sid="test-"+Guid.NewGuid().ToString("N"); await using var provider=new ProviderFixture(sid);
            await using var runtime=new PluginTaskRuntime([new ProgramPlugin(),new GameModePlugin(["program.start"])],store,sid,activity.Engine);
            await runtime.InitializeAsync();
            Assert.True(runtime.ReadGameMode().Active); Assert.Equal(0,provider.Calls);
            // Saving an enabled rule while state is already true is a new baseline.
            await runtime.SaveAsync(saved with {Title="edited"},default); Assert.Equal(0,provider.Calls);
            var off=await runtime.SaveGameModeAsync(runtime.ReadGameMode().Settings with {ManualEnabled=false},default);
            Assert.False(off.Active);
            await runtime.SaveGameModeAsync(off.Settings with {ManualEnabled=true},default);
            await provider.NextCommand(); Assert.Equal(1,provider.Calls);
            await runtime.EnsureSafeToStopAsync(default);
        } finally {Directory.Delete(dir,true);}
    }
    [Fact(Timeout=20000)]
    public async Task AutomaticTracksAllTargetsUnknownAndManualOverride()
    {
        var dir=Path.Combine(Path.GetTempPath(),"axiom-game-"+Guid.NewGuid().ToString("N"));
        try {
            using var activity=new AutomationFixture(); using var store=new SqliteAutomationTaskStore(Path.Combine(dir,"tasks.db"));
            await store.SaveGameModeSettingsAsync(new(AutomaticEnabled:true,ProcessNames:["one.exe","two.exe"]),default);
            var sid="test-"+Guid.NewGuid().ToString("N"); await using var provider=new ProviderFixture(sid);
            await using var runtime=new PluginTaskRuntime([new ProgramPlugin(),new GameModePlugin(["program.start"])],store,sid,activity.Engine);
            await runtime.InitializeAsync(); await provider.Observations.Reader.ReadAsync();
            await WaitUntil(()=>Task.FromResult(runtime.ReadGameMode().Active==false));
            void Send(string name,bool? running)=>provider.Changes.Writer.TryWrite(new(new("process.running",name),running is null?StateQuality.Unknown:StateQuality.Known,"axiom.bool",1,Boolean:running));
            Send("one.exe",true); await WaitUntil(()=>Task.FromResult(runtime.ReadGameMode().Active==true));
            Send("two.exe",true); Send("one.exe",false); await WaitUntil(()=>Task.FromResult(runtime.ReadGameMode().Active==true));
            Send("two.exe",null); await WaitUntil(()=>Task.FromResult(runtime.ReadGameMode().Active is null));
            var manual=await runtime.SaveGameModeAsync(runtime.ReadGameMode().Settings with {ManualEnabled=true},default); Assert.True(manual.Active);
            var auto=await runtime.SaveGameModeAsync(manual.Settings with {ManualEnabled=false},default); Assert.Null(auto.Active);
            Send("two.exe",false); await WaitUntil(()=>Task.FromResult(runtime.ReadGameMode().Active==false));
            await runtime.EnsureSafeToStopAsync(default);
        } finally {Directory.Delete(dir,true);}
    }
    [Fact(Timeout=20000)]
    public async Task AutomaticInitialAndReconnectSnapshotsDoNotReplayCommands()
    {
        var dir=Path.Combine(Path.GetTempPath(),"axiom-game-"+Guid.NewGuid().ToString("N"));
        try {
            using var activity=new AutomationFixture(); using var store=new SqliteAutomationTaskStore(Path.Combine(dir,"tasks.db"));
            await store.SaveGameModeSettingsAsync(new(AutomaticEnabled:true,ProcessNames:["one.exe"]),default);
            await store.SaveAsync(Rule(),default);
            var sid="test-"+Guid.NewGuid().ToString("N"); await using var provider=new ProviderFixture(sid,waitForReady:true) {InitialRunning=true};
            await using var runtime=new PluginTaskRuntime([new ProgramPlugin(),new GameModePlugin(["program.start"])],store,sid,activity.Engine);
            await runtime.InitializeAsync(); await provider.Observations.Reader.ReadAsync(); provider.Ready.SetResult();
            await WaitUntil(()=>Task.FromResult(runtime.ReadGameMode().Active==true)); Assert.Equal(0,provider.Calls);
            provider.Changes.Writer.TryWrite(new(new("process.running","one.exe"),StateQuality.Known,"axiom.bool",1,Boolean:false));
            await WaitUntil(()=>Task.FromResult(runtime.ReadGameMode().Active==false));
            provider.Changes.Writer.TryWrite(new(new("process.running","one.exe"),StateQuality.Known,"axiom.bool",1,Boolean:true));
            await provider.NextCommand(); Assert.Equal(1,provider.Calls);
            provider.DropObservations(); await WaitUntil(()=>Task.FromResult(runtime.ReadGameMode().Active is null));
            await provider.Observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            await WaitUntil(()=>Task.FromResult(runtime.ReadGameMode().Active==true)); Assert.Equal(1,provider.Calls);
            await runtime.EnsureSafeToStopAsync(default);
        }finally {Directory.Delete(dir,true);}
    }
    [Fact]
    public async Task SettingsPersistWithConflictsAndProtocolRejectsForeignFields()
    {
        var dir=Path.Combine(Path.GetTempPath(),"axiom-game-"+Guid.NewGuid().ToString("N"));var path=Path.Combine(dir,"tasks.db");
        try {
            using(var store=new SqliteAutomationTaskStore(path)) {
                Assert.False(store.ReadGameModeSettings().ManualEnabled);
                var saved=await store.SaveGameModeSettingsAsync(new(ProcessNames:["GAME.exe","game.exe"]),default);
                Assert.Equal(1,saved.Version);Assert.Single(saved.ProcessNames!);
                await Assert.ThrowsAsync<InvalidOperationException>(()=>store.SaveGameModeSettingsAsync(new(),default));
            }
            using(var store=new SqliteAutomationTaskStore(path)) Assert.Equal("game.exe",Assert.Single(store.ReadGameModeSettings().ProcessNames!));
            var now=DateTimeOffset.UtcNow;var request=new AutomationMessage(1,"save-game-mode","test",now.AddSeconds(5),GameModeSettings:new());
            AutomationProtocol.ValidateRequest(request,now);
            Assert.Throws<InvalidDataException>(()=>AutomationProtocol.ValidateRequest(request with {Task=Rule()},now));
            Assert.Throws<InvalidDataException>(()=>AutomationProtocol.ValidateRequest(request,now,true));
            Assert.Throws<ArgumentException>(()=>GameModeCapabilities.Normalize(new(AutomaticEnabled:true)));
        }finally {Directory.Delete(dir,true);}
    }
    private static async Task WaitUntil(Func<Task<bool>> predicate)
    {for(var i=0;i<400;i++){if(await predicate())return;await Task.Delay(10);}throw new TimeoutException("Game mode state timeout");}
    private sealed class ProviderFixture : IAsyncDisposable
    {
        private readonly string name, owner = WindowsIdentity.GetCurrent().User!.Value;
        private readonly CancellationTokenSource cancel = new();
        private readonly List<NamedPipeServerStream> streams = [];
        private readonly List<Task> handlers = [];
        private readonly Task server;
        private int calls, receipts, cancelledCommands;
        public bool InitialRunning { get; set; }
        public Channel<ProviderSample> Changes { get; } = Channel.CreateUnbounded<ProviderSample>();
        public int Calls => Volatile.Read(ref calls);
        public int Receipts => Volatile.Read(ref receipts);
        public int CancelledCommands => Volatile.Read(ref cancelledCommands);
        public Channel<ProviderMessage> Observations { get; } = Channel.CreateUnbounded<ProviderMessage>();
        private readonly Channel<ProviderMessage> commands = Channel.CreateUnbounded<ProviderMessage>();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCommands { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderFixture(string sid, bool waitForReady = false, bool holdCommands = false)
        { name = ProviderProtocol.PipeName(sid); if (!waitForReady) Ready.SetResult(); if (!holdCommands) ReleaseCommands.SetResult(); server = Serve(); }
        public Task<ProviderMessage> NextCommand() => commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public void DropObservations() { lock (streams) foreach (var pipe in streams) pipe.Dispose(); }
        private async Task Serve()
        {
            while (!cancel.IsCancellationRequested)
            {
                var pipe = LocalPipeFactory.Create(name, owner);
                try { await pipe.WaitForConnectionAsync(cancel.Token); }
                catch (OperationCanceledException) { pipe.Dispose(); break; }
                handlers.Add(Handle(pipe));
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
                    Observations.Writer.TryWrite(request); await Ready.Task.WaitAsync(cancel.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "snapshot", request.RequestId, Samples: request.Resources!.Select(r => new ProviderSample(r, StateQuality.Known, "axiom.bool", 1, Boolean: InitialRunning)).ToArray()), cancel.Token);
                    var disconnected = ProviderProtocol.ReadAsync(pipe, cancel.Token);
                    long sequence = 1;
                    while (!cancel.IsCancellationRequested) {
                        var next = Changes.Reader.ReadAsync(cancel.Token).AsTask();
                        if (await Task.WhenAny(next, disconnected) == disconnected) { await disconnected; break; }
                        var sample = await next;
                        await ProviderProtocol.WriteAsync(pipe, new(1, "update", request.RequestId, Samples: [sample], Sequence: sequence++), cancel.Token);
                    }
                }
                else
                {
                    Interlocked.Increment(ref calls); commands.Writer.TryWrite(request);
                    var disconnected = ProviderProtocol.ReadAsync(pipe, cancel.Token);
                    if (await Task.WhenAny(ReleaseCommands.Task, disconnected) != ReleaseCommands.Task)
                    { Interlocked.Increment(ref cancelledCommands); await disconnected; return; }
                    await ReleaseCommands.Task.WaitAsync(cancel.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "result", request.RequestId, CommandId: request.CommandId, Status: OutcomeStatus.Succeeded, Reason: "fixture action completed"), cancel.Token);
                    Interlocked.Increment(ref receipts); await disconnected;
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { cancel.Cancel(); DropObservations(); await server; cancel.Dispose(); }
    }
}
