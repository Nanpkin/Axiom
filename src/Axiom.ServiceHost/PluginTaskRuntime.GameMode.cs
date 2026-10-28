using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
namespace Axiom.ServiceHost;
internal sealed partial class PluginTaskRuntime
{
    private GameModeSettings gameSettings = new();
    private readonly Dictionary<string, bool?> gameProcesses = new(StringComparer.OrdinalIgnoreCase);
    private SourceSession? gameSource;
    private long gameSequence;
    private bool? gameActive;
    private bool gamePublished;
    private ResourceKey GameKey => new(GameModeCapabilities.State, "session", scope);
    private ResourceKey[] GameProcessKeys() => gameSettings.AutomaticEnabled ? [.. gameSettings.ProcessNames!.Select(n => new ResourceKey("process.running", n, scope))] : [];
    private void InitializeGameMode()
    {
        gameSettings = database.ReadGameModeSettings();
        if (!bindings.ContainsKey(GameModeCapabilities.State)) return;
        gameSource = engine.RegisterSource("host-game-mode", [GameKey]);
        PublishGameMode(true);
    }
    private void PublishGameMode(bool baseline = false)
    {
        var activeValue = GameModeCapabilities.Active(gameSettings, (gameSettings.ProcessNames ?? []).Select(n => gameProcesses.GetValueOrDefault(n)).ToArray());
        if (gamePublished && activeValue == gameActive) return;
        gameActive = activeValue;
        if (gameSource is null) return;
        gamePublished = true;
        var observation = new Observation(GameKey, gameActive is null ? StateQuality.Unknown : StateQuality.Known,
            gameActive is { } active ? StateValue.From(new("axiom.bool", 1), active) : null, DateTimeOffset.UtcNow);
        if (baseline) engine.ApplySnapshot(gameSource, gameSequence++, [observation]);
        else engine.Update(gameSource, gameSequence++, observation);
    }
    private void ObserveGameProcesses(Observation[] observations, bool baseline)
    {
        if (!gameSettings.AutomaticEnabled) return;
        var related = observations.Where(o => o.Key.CapabilityId == "process.running" && gameSettings.ProcessNames!.Contains(o.Key.ResourceId, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (related.Length == 0) return;
        foreach (var observation in related)
            gameProcesses[observation.Key.ResourceId] = observation.Quality == StateQuality.Known ? observation.Value?.Boolean : null;
        PublishGameMode(baseline);
    }
    public GameModeSnapshot ReadGameMode()
    {
        lock (statusGate) return new(gameSettings with { ProcessNames = [.. gameSettings.ProcessNames ?? []] }, gameActive,
            gameActive is null ? "指定程序状态未知，等待会话连接。" : gameActive == true ? gameSettings.ManualEnabled ? "已手动开启。" : "指定程序正在运行。" : "游戏模式已关闭。");
    }
    public async Task<GameModeSnapshot> SaveGameModeAsync(GameModeSettings settings, CancellationToken token)
    {
        settings = GameModeCapabilities.Normalize(settings);
        await gate.WaitAsync(token);
        try
        {
            if (stopping) throw new InvalidOperationException("后台正在停止。");
            var automatic = settings.AutomaticEnabled ? settings.ProcessNames! : [];
            var required = engine.GetObservationPlan().References.Keys.Where(k => k.CapabilityId != GameModeCapabilities.State)
                .Concat(automatic.Select(n => new ResourceKey("process.running", n, scope))).Distinct().Count();
            if (required > 128) throw new ArgumentException("启用的任务与游戏模式最多检测128个资源。");
            var saved = await database.SaveGameModeSettingsAsync(settings, token);
            lock (statusGate)
            {
                foreach (var name in gameProcesses.Keys.Where(n => !automatic.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray()) gameProcesses.Remove(name);
                gameSettings = saved;
                PublishGameMode();
            }
            await RefreshObservationAsync();
            return ReadGameMode();
        }
        finally { gate.Release(); }
    }
}
