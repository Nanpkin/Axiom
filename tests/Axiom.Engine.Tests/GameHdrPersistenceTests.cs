using Axiom.Contracts;
using Axiom.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Axiom.Engine.Tests;

public sealed class GameHdrPersistenceTests
{
    [Fact]
    public async Task SavedRuleSurvivesRestartAndVersionConflictDoesNotOverwrite()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-game-hdr-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var original = new GameHdrRuleDto(Guid.NewGuid().ToString("D"), 0, "原神", "yuanshen.exe",
                @"\\?\DISPLAY#TEST", true, false);
            GameHdrRuleDto saved;
            using (var first = new SqliteGameHdrStore(path)) saved = await first.SaveAsync(original);
            Assert.Equal(1, saved.Version);
            using (var second = new SqliteGameHdrStore(path))
            {
                Assert.Equal(saved, Assert.Single(await second.ListAsync()));
                var paused = await second.SaveAsync(saved with { Paused = true });
                Assert.True(paused.Paused);
                await Assert.ThrowsAsync<InvalidOperationException>(() => second.SaveAsync(saved with { Title = "stale" }));
                Assert.Equal(paused, Assert.Single(await second.ListAsync()));
                await second.DeleteAsync(paused.Id, paused.Version);
                Assert.Empty(await second.ListAsync());
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task MultipleNamesSurviveRestartAndVersionOneDatabaseMigratesWithoutCreatingBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-game-hdr-multi-" + Guid.NewGuid().ToString("N") + ".db");
        var id = Guid.NewGuid().ToString("D");
        try
        {
            using (var old = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString()))
            {
                old.Open();
                using var command = old.CreateCommand();
                command.CommandText = "CREATE TABLE game_hdr_rules (id TEXT PRIMARY KEY, version INTEGER NOT NULL, title TEXT NOT NULL, process_name TEXT NOT NULL, display_path TEXT NOT NULL, hdr_enabled INTEGER NOT NULL, paused INTEGER NOT NULL); PRAGMA user_version=1;";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO game_hdr_rules VALUES ($id,1,'old','first.exe','display',1,0);";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            using (var store = new SqliteGameHdrStore(path))
            {
                var oldRule = Assert.Single(await store.ListAsync());
                Assert.Equal(["first.exe"], ProcessNameSelection.FromRule(oldRule));
                var updated = await store.SaveAsync(oldRule with { ProcessNamesText = "first.exe\nsecond.exe" });
                Assert.Equal(2, updated.Version);
            }
            using (var reopened = new SqliteGameHdrStore(path))
            {
                var saved = Assert.Single(await reopened.ListAsync());
                Assert.Equal(["first.exe", "second.exe"], ProcessNameSelection.FromRule(saved));
                Assert.Equal(2, saved.Version);
            }
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".before-multiprocess-*.bak"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".before-multiprocess-*.bak")) File.Delete(backup);
        }
    }

    [Fact]
    public async Task FrameRejectsOversizeBeforeAllocatingPayload()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(GameHdrProtocol.MaximumFrame + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => GameHdrProtocol.ReadAsync(stream, CancellationToken.None));
    }
}
