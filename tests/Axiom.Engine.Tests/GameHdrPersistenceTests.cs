using Axiom.Contracts;
using Axiom.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Axiom.Engine.Tests;

public sealed class GameHdrPersistenceTests
{
    [Fact]
    public async Task NamedDisplaySurvivesConnectionChangesAndLegacyRulesResolveOnlyOneMatchingModel()
    {
        const string old = @"\\?\DISPLAY#SYN1234#old-instance#{synthetic}";
        const string current = @"\\?\DISPLAY#SYN1234#new-instance#{synthetic}";
        GameHdrDisplayDto Display(string path, string name = "Synthetic HDR") => new(name, path, true, false, false, null);
        var displays = HdrDisplayTargets.NameSelections([Display(current)]);
        var resolved = HdrDisplayTargets.Resolve(displays, old);
        Assert.NotNull(resolved); Assert.Equal(current, resolved.ConnectionPath);
        Assert.Equal(HdrDisplayTargets.Named("Synthetic HDR"), resolved.Path);
        var moved = HdrDisplayTargets.NameSelections([Display(@"\\?\DISPLAY#SYN1234#another-port#{synthetic}")]);
        Assert.Equal(resolved.Path, HdrDisplayTargets.Resolve(moved, resolved.Path)!.Path);
        var duplicate = HdrDisplayTargets.NameSelections([Display(current), Display(old)]);
        Assert.Null(HdrDisplayTargets.Resolve(duplicate, resolved.Path));
        Assert.Null(HdrDisplayTargets.Resolve(duplicate, @"\\?\DISPLAY#SYN1234#missing-instance#{synthetic}"));
        Assert.Equal(current, HdrDisplayTargets.Resolve(duplicate, current)!.Path);
        Assert.Null(HdrDisplayTargets.Resolve(HdrDisplayTargets.NameSelections([Display(@"\\?\DISPLAY#OTHER00#port#{synthetic}")]), old));
        Assert.Contains("连接", GameHdrTaskPresentation.UnavailableDisplayReason(null));
        Assert.Contains("不支持", GameHdrTaskPresentation.UnavailableDisplayReason(Display(current) with { Supported = false }));
        Assert.Contains("不表示", GameHdrTaskPresentation.UnavailableDisplayReason(Display(current) with { Supported = null }));
        Assert.Null(GameHdrTaskPresentation.UnavailableDisplayReason(Display(current)));
        using var wire = new MemoryStream();
        await GameHdrProtocol.WriteAsync(wire, new(1, "displays", "synthetic", Displays: displays), default); wire.Position = 0;
        Assert.Equal(current, (await GameHdrProtocol.ReadAsync(wire, default))!.Displays![0].ConnectionPath);
    }

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
