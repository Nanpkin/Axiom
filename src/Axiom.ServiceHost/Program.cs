using System.IO.Pipes;
using System.Security.Principal;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;

namespace Axiom.ServiceHost;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (args.Length > 1 || args.Length == 1 && args[0] != "--background")
            throw new ArgumentException("Use --background or no arguments.");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("No OS user SID.");
        using var single = new Mutex(true, @"Local\Axiom.GameHdr.Host." + sid, out var firstHost);
        if (!firstHost) throw new InvalidOperationException("Axiom host is already running in this session.");
        var clock = new RuntimeClock(TimeProvider.System);
        using var scheduler = new DeadlineScheduler(clock);
        var gameExecutor = new GameHdrExecutorBridge();
        using var engine = new EngineHost(clock, scheduler, gameExecutor);
        engine.Start();
        var ruleDatabase = RuleDraftDataPath.Prepare(sid);
        if (File.Exists(ruleDatabase)) RuleDraftDataPath.ProtectFile(ruleDatabase, sid);
        using var drafts = new SqliteRuleDraftStore(ruleDatabase);
        RuleDraftDataPath.ProtectFile(ruleDatabase, sid);
        engine.ConfigureRuleDrafts(drafts, sid);
        var gameDatabase = Path.Combine(Path.GetDirectoryName(ruleDatabase)!, "game-hdr.db");
        using var gameStore = new SqliteGameHdrStore(gameDatabase);
        RuleDraftDataPath.ProtectFile(gameDatabase, sid);
        var shortcutDatabase = Path.Combine(Path.GetDirectoryName(ruleDatabase)!, "shortcuts.db");
        if (File.Exists(shortcutDatabase)) RuleDraftDataPath.ProtectFile(shortcutDatabase, sid);
        using var shortcutStore = new SqliteShortcutStore(shortcutDatabase);
        RuleDraftDataPath.ProtectFile(shortcutDatabase, sid);
        var groupDatabase = Path.Combine(Path.GetDirectoryName(ruleDatabase)!, "process-groups.db");
        if (File.Exists(groupDatabase)) RuleDraftDataPath.ProtectFile(groupDatabase, sid);
        using var groupStore = new SqliteProcessGroupStore(groupDatabase);
        RuleDraftDataPath.ProtectFile(groupDatabase, sid);
        using var steamCatalog = new SteamCatalog(WorkbenchUtilities.LocateSteam);
        await using var utilities = new WorkbenchUtilities(groupStore, steamCatalog, (title, detail, success) =>
            engine.RecordActivity(new(DateTimeOffset.UtcNow, "Steam 工具", title, detail, success ? ActivityLevel.Success : ActivityLevel.Warning)));
        var activityDatabase = Path.Combine(Path.GetDirectoryName(ruleDatabase)!, "activity.db");
        SqliteActivityStore? activityStorage = null;
        string? activityError = null;
        try
        {
            if (File.Exists(activityDatabase)) RuleDraftDataPath.ProtectFile(activityDatabase, sid);
            activityStorage = new SqliteActivityStore(activityDatabase);
            RuleDraftDataPath.ProtectFile(activityDatabase, sid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Security.SecurityException)
        { activityStorage?.Dispose(); activityStorage = null; activityError = error.Message; }
        using var activityStore = activityStorage;
        await using var activity = new ActivityJournal(activityStore, activityError);
        engine.ConfigureActivityHistory(activity, sid);
        engine.RecordActivity(new(DateTimeOffset.UtcNow, "后台", "后台已启动", "已加载保存的自动化配置，正在连接会话程序。"));
        using var game = new GameHdrRuntime(engine, gameStore, sid);
        gameExecutor.Runtime = game;
        await game.InitializeAsync();
        var pluginDatabase = Path.Combine(Path.GetDirectoryName(ruleDatabase)!, "tasks.db");
        using var pluginStore = new SqliteAutomationTaskStore(pluginDatabase);
        RuleDraftDataPath.ProtectFile(pluginDatabase, sid);
        await using var plugins = new PluginTaskRuntime([new ProgramPlugin(), new AxiomStartupPlugin(), new WindowPlugin(), new TrayPlugin(), new KeyboardPlugin(), new SystemEventPlugin(), new IntervalPlugin(), new DailyTimePlugin(), new FileArrivalPlugin(), new AudioPlaybackPlugin(), new MicrophonePlugin(), new OnceTimePlugin(), new AudioPlugin(), new OpenTargetPlugin(), new ForegroundPlugin(), new WindowPresencePlugin()], pluginStore, sid, engine, WindowsBootIdentity.Read);
        await plugins.InitializeAsync();
        var key = new ResourceKey("windows.service.status", ServiceStatusProtocol.ServiceName, ResourceScope.Machine);
        engine.ConfigureDisplayObservation(key);
        var observer = new ServiceStatusObserver(new WindowsServiceStatusNative());
        var coordinator = new ObservationCoordinator(engine, observer, key);
        using var exit = new CancellationTokenSource();
        Console.WriteLine($"Axiom status host: {ServiceStatusProtocol.ServiceName}; pipe {ServiceStatusProtocol.PipeName(sid)}");
        Console.WriteLine(args.Length == 1 ? "Game HDR background preview is running." :
            "Press Enter to exit. No Windows service control operations are available.");
        var server = new PipeServer(engine, coordinator, sid, exit.Token);
        var serve = server.RunAsync();
        var ruleServer = new RuleDraftPipeServer(engine, sid, exit.Token);
        var serveRules = ruleServer.RunAsync();
        var gameServer = new GameHdrPipeServer(game, sid, exit);
        var serveGame = gameServer.RunAsync();
        var loginStartup = new LoginStartupRegistration(AxiomInstallLayout.ForComponent(AppContext.BaseDirectory).Launcher, Microsoft.Win32.Registry.CurrentUser);
        try { loginStartup.MigrateLegacy(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { engine.RecordActivity(new(DateTimeOffset.UtcNow, "设置", "登录启动迁移未完成", error.Message)); }
        var usageDatabase = Path.Combine(Path.GetDirectoryName(ruleDatabase)!, "usage.db");
        if (File.Exists(usageDatabase)) RuleDraftDataPath.ProtectFile(usageDatabase, sid);
        using var usageJournal = new UsageJournal(usageDatabase);
        RuleDraftDataPath.ProtectFile(usageDatabase, sid);
        await using var usage = new UsageObservationController(usageJournal, new AgentUsageObservationSource(sid));
        var taskServer = new AutomationPipeServer([new GameHdrTaskBackend(game, sid,
            ResourceScope.Session(sid, System.Diagnostics.Process.GetCurrentProcess().SessionId)), plugins], new AutomationRuntimeLifetime(game, plugins), sid, exit,
            loginStartup,
            new ShortcutCoordinator(shortcutStore, new ShortcutAgentClient(sid)), engine, utilities, usage);
        var serveTasks = taskServer.RunAsync();
        utilities.StartStartupScan();
        try { await game.RefreshAgentAsync(exit.Token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        { Console.WriteLine("Game HDR session agent not connected yet: " + error.Message); }
        if (args.Length == 1)
        {
            try { await Task.Delay(Timeout.Infinite, exit.Token); }
            catch (OperationCanceledException) when (exit.IsCancellationRequested) { }
        }
        else await Task.Run(Console.ReadLine).ConfigureAwait(false);
        exit.Cancel();
        await serve.ConfigureAwait(false);
        await serveRules.ConfigureAwait(false);
        await serveGame.ConfigureAwait(false);
        await serveTasks.ConfigureAwait(false);
        await coordinator.ShutdownAsync().ConfigureAwait(false);
        try { await game.StopAgentAsync(); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
        { Console.Error.WriteLine("Agent stop not confirmed: " + error.Message); }
        engine.Stop();
    }
}

internal sealed class ObservationCoordinator(EngineHost engine, ServiceStatusObserver observer, ResourceKey key)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<DisplayDemandLease> ObserveAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var first = engine.DisplayDemandCount == 0;
            var lease = engine.AcquireDisplayObservation(key);
            try { if (first) await observer.StartAsync(Publish).ConfigureAwait(false); }
            catch { lease.Dispose(); throw; }
            return lease;
        }
        finally { gate.Release(); }
    }

    public async Task PauseAsync(DisplayDemandLease lease)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lease.Dispose();
            if (engine.DisplayDemandCount == 0)
            {
                var release = observer.StopAsync();
                await release.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }

    public async Task RetryAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (engine.DisplayDemandCount == 0) throw new InvalidOperationException("No observation demand.");
            await observer.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await observer.StartAsync(Publish).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task ShutdownAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await observer.StopAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private void Publish(ServiceStatusSample sample)
    {
        if (sample.State is null)
            engine.PublishDisplaySample(key, StateQuality.Unknown, null, sample.Error);
        else
            engine.PublishDisplaySample(key, StateQuality.Known,
                StateValue.From(new ValueSchema("windows.service.status", 1), sample.State));
    }
}

internal sealed class PipeServer(EngineHost engine, ObservationCoordinator coordinator, string sid, CancellationToken stopping)
{
    private readonly HashSet<Task> clients = [];
    private readonly object clientGate = new();
    private readonly string pipeName = ServiceStatusProtocol.PipeName(sid);

    public async Task RunAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            var server = LocalPipeFactory.Create(pipeName, sid);
            try { await server.WaitForConnectionAsync(stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { server.Dispose(); break; }
            lock (clientGate)
            {
                clients.RemoveWhere(task => task.IsCompleted);
                if (clients.Count >= 8) { server.Dispose(); continue; }
                // Ownership moves to the connection task; it disposes the stream.
                var connection = Task.Run(() => HandleAsync(server));
                clients.Add(connection);
            }
        }
        Task[] remaining;
        lock (clientGate) remaining = [.. clients];
        await Task.WhenAll(remaining).ConfigureAwait(false);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            DisplayDemandLease? lease = null;
            try
            {
                var revision = engine.ReadDisplayObservation().Revision;
                var requestCount = 0;
                Task<ServiceMessage?> read = ServiceStatusProtocol.ReadAsync(pipe, stopping);
                while (pipe.IsConnected && !stopping.IsCancellationRequested)
                {
                    using var changeCancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    var change = lease is null ? null : engine.WaitForDisplayChangeAsync(revision, changeCancel.Token);
                    ServiceMessage? done;
                    if (change is not null)
                    {
                        var completed = await Task.WhenAny(read, change).ConfigureAwait(false);
                        if (completed == change)
                        {
                            revision = await change.ConfigureAwait(false);
                            await SendStatusAsync(pipe, "status", "update").ConfigureAwait(false);
                            continue;
                        }
                        changeCancel.Cancel();
                        done = await read.ConfigureAwait(false);
                    }
                    else done = await read.ConfigureAwait(false);
                    if (done is null) break;
                    string? actualSid = null;
                    pipe.RunAsClient(() => actualSid = WindowsIdentity.GetCurrent().User?.Value);
                    if (actualSid != sid) throw new UnauthorizedAccessException("Client SID rejected.");
                    ServiceStatusProtocol.ValidateRequest(done, DateTimeOffset.UtcNow);
                    if (++requestCount > 64) throw new InvalidDataException("Request limit exceeded.");
                    if (done.Type == "observe")
                    {
                        lease ??= await coordinator.ObserveAsync().ConfigureAwait(false);
                    }
                    else if (done.Type == "pause" && lease is not null)
                    {
                        await coordinator.PauseAsync(lease).ConfigureAwait(false);
                        lease = null;
                    }
                    else if (done.Type == "retry")
                    {
                        if (lease is null) throw new InvalidOperationException("Retry requires observation.");
                        await coordinator.RetryAsync().ConfigureAwait(false);
                    }
                    revision = engine.ReadDisplayObservation().Revision;
                    await SendStatusAsync(pipe, "snapshot", done.RequestId).ConfigureAwait(false);
                    read = ServiceStatusProtocol.ReadAsync(pipe, stopping);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                Console.Error.WriteLine($"Pipe client rejected: {error.Message}");
                try { await SendErrorAsync(pipe, error.Message).ConfigureAwait(false); }
                catch (Exception writeError) when (writeError is IOException or OperationCanceledException or InvalidOperationException) { }
            }
            finally
            {
                if (lease is not null)
                {
                    try { await coordinator.PauseAsync(lease).ConfigureAwait(false); }
                    catch (TimeoutException) { /* native ownership remains held until worker exits */ }
                }
            }
        }
    }

    private Task SendStatusAsync(Stream pipe, string type, string id)
    {
        var status = engine.ReadDisplayObservation();
        var state = status.Observing ? status.State?.Observation : null;
        return SendAsync(pipe, new(1, 0, 1, type, id, ServiceStatusProtocol.Scope, ServiceStatusProtocol.ServiceName,
            state?.Quality == StateQuality.Known ? state.Value?.Text : null, status.Error,
            status.LastSuccessAt, status.Revision, status.Observing));
    }

    private Task SendErrorAsync(Stream pipe, string error) => SendAsync(pipe,
        new(1, 0, 1, "error", "rejected", ServiceStatusProtocol.Scope, ServiceStatusProtocol.ServiceName, Error: error));

    private static async Task SendAsync(Stream pipe, ServiceMessage message)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ServiceStatusProtocol.WriteAsync(pipe, message, deadline.Token).ConfigureAwait(false);
    }
}
