using System.Text.Json;
using Axiom.Contracts;
namespace Axiom.Infrastructure;
public sealed partial class SqliteAutomationTaskStore
{
    private void CreateGameModeSettings()
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS game_mode(id INTEGER PRIMARY KEY CHECK(id=1), configuration TEXT NOT NULL);"; cmd.ExecuteNonQuery();
    }
    public GameModeSettings ReadGameModeSettings()
    {
        gate.Wait();
        try { using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT configuration FROM game_mode WHERE id=1;";
            return GameModeCapabilities.Normalize(cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<GameModeSettings>(json, TaskJson)! : new()); }
        finally { gate.Release(); }
    }
    public async Task<GameModeSettings> SaveGameModeSettingsAsync(GameModeSettings settings, CancellationToken token)
    {
        settings = GameModeCapabilities.Normalize(settings); await gate.WaitAsync(token);
        try {
            using var tx = connection.BeginTransaction(); using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT configuration FROM game_mode WHERE id=1;";
            var current = cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<GameModeSettings>(json, TaskJson)!.Version : 0;
            if (current != settings.Version) throw new InvalidOperationException("游戏模式已在别处更改，请刷新后重试。");
            var saved = settings with { Version = checked(current + 1) };
            cmd.CommandText = "INSERT INTO game_mode(id,configuration) VALUES(1,$config) ON CONFLICT(id) DO UPDATE SET configuration=$config;";
            cmd.Parameters.AddWithValue("$config", JsonSerializer.Serialize(saved)); cmd.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); tx.Commit(); return saved;
        } finally { gate.Release(); }
    }
}
