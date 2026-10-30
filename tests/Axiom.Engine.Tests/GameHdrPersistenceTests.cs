using Axiom.Contracts;
using Axiom.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Axiom.Engine.Tests;

public sealed class GameHdrPersistenceTests
{
    [Fact(Timeout = 10000)]
    public async Task NewRoundUsesFreshEventStateForExpectedValueAndRestoresThatBaselineInBothDirections()
    {
        foreach (var initial in new[] { false, true })
        {
            var path = Path.Combine(Path.GetTempPath(), "axiom-hdr-fresh-" + Guid.NewGuid().ToString("N") + ".db");
            var target = HdrDisplayTargets.Named("Synthetic HDR");
            var display = new GameHdrDisplayDto("Synthetic HDR", target, true, !initial, !initial, null, "synthetic-connection");
            var generation = ""; var expected = new List<bool>();
            Task<GameHdrMessage> Request(GameHdrMessage request, CancellationToken token)
            {
                if (request.Type == "watch") generation = request.RequestId;
                if (request.Type == "apply")
                {
                    expected.Add(request.ExpectedEnabled!.Value);
                    if (display.Enabled != request.ExpectedEnabled)
                        return Task.FromResult(new GameHdrMessage(1, "applied", request.RequestId, Error: "stale expected state", Displays: [display]));
                    display = display with { Enabled = request.Enabled, Active = request.Enabled };
                }
                return Task.FromResult(new GameHdrMessage(1, request.Type == "apply" ? "applied" : request.Type, request.RequestId, Displays: [display]));
            }
            try
            {
                using var store = new SqliteGameHdrStore(path);
                await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "synthetic", "synthetic.exe", target, !initial, false));
                var clock = new FakeClock();
                using var engine = new EngineHost(clock, new FakeScheduler(clock), new FakeExecutor()); engine.Start();
                using var runtime = new Axiom.ServiceHost.GameHdrRuntime(engine, store, "synthetic-owner", Request);
                await runtime.InitializeAsync(); await runtime.RefreshAgentAsync(default);
                await runtime.ProcessEventAsync(new(1, "event", "snapshot", Generation: generation, Reason: "snapshot-complete", Displays: [display]), default);
                display = display with { Enabled = initial, Active = initial }; // Changed while no game was controlled.
                await runtime.ProcessEventAsync(new(1, "event", "start", Generation: generation, ProcessName: "synthetic.exe", ProcessId: 42,
                    StartedAtUtc: DateTimeOffset.UnixEpoch, Running: true, Displays: [display]), default);
                var key = new Axiom.Domain.ResourceKey("display.hdr", target,
                    Axiom.Domain.ResourceScope.Session("synthetic-owner", System.Diagnostics.Process.GetCurrentProcess().SessionId));
                async Task Apply(bool value)
                {
                    var result = await runtime.ApplyDesiredAsync(new(key, Axiom.Domain.StateValue.From(new("axiom.bool", 1), value), "synthetic-owner", 0, 1, "synthetic"), default);
                    Assert.Equal(Axiom.Domain.OutcomeStatus.Succeeded, result.Status);
                }
                await Apply(!initial);
                await runtime.ProcessEventAsync(new(1, "event", "exit", Generation: generation, ProcessName: "synthetic.exe", ProcessId: 42,
                    StartedAtUtc: DateTimeOffset.UnixEpoch, Running: false, Displays: [display]), default);
                await Apply(initial);
                Assert.Equal(new[] { initial, !initial }, expected); Assert.Equal(initial, display.Enabled);
                await runtime.EnsureSafeToStopAsync(default);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }

    [Fact(Timeout = 10000)]
    public async Task ConfirmedReceiptSurvivesCancellationAndExitButCannotReacquireAnEndedRound()
    {
        foreach (var mode in new[] { "cancel", "exit", "restore-exit", "external", "disconnect" })
        {
            var path = Path.Combine(Path.GetTempPath(), "axiom-hdr-receipt-" + Guid.NewGuid().ToString("N") + ".db");
            var target = HdrDisplayTargets.Named("Synthetic HDR");
            var display = new GameHdrDisplayDto("Synthetic HDR", target, true, false, false, null, "synthetic-connection");
            var generation = ""; var applies = 0;
            Axiom.ServiceHost.GameHdrRuntime? runtime = null;
            using var cancel = new CancellationTokenSource();
            GameHdrMessage Exit() => new(1, "event", "exit", Generation: generation, ProcessName: "synthetic.exe", ProcessId: 42,
                StartedAtUtc: DateTimeOffset.UnixEpoch, Running: false, Displays: [display]);
            async Task<GameHdrMessage> Request(GameHdrMessage request, CancellationToken token)
            {
                if (request.Type == "watch") generation = request.RequestId;
                if (request.Type == "apply")
                {
                    applies++;
                    if (display.Enabled != request.ExpectedEnabled) return new(1, "applied", request.RequestId, Error: "stale expected state");
                    display = display with { Enabled = request.Enabled, Active = request.Enabled };
                    if (applies == (mode == "restore-exit" ? 2 : 1))
                    {
                        cancel.Cancel();
                        if (mode is "exit" or "restore-exit") await runtime!.ProcessEventAsync(Exit(), default);
                        if (mode == "disconnect") await runtime!.AgentDisconnectedAsync("connection");
                    }
                }
                return new(1, request.Type == "apply" ? "applied" : request.Type, request.RequestId, Displays: [display]);
            }
            try
            {
                using var store = new SqliteGameHdrStore(path);
                await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "synthetic", "synthetic.exe", target, true, false));
                var clock = new FakeClock();
                using var engine = new EngineHost(clock, new FakeScheduler(clock), new FakeExecutor()); engine.Start();
                using var owner = runtime = new(engine, store, "synthetic-owner", Request);
                await runtime.InitializeAsync(); await runtime.RefreshAgentAsync(default); await runtime.MarkAgentConnectionAsync("connection");
                await runtime.ProcessEventAsync(new(1, "event", "snapshot", Generation: generation, Reason: "snapshot-complete", Displays: [display]), default);
                await runtime.ProcessEventAsync(new(1, "event", "start", Generation: generation, ProcessName: "synthetic.exe", ProcessId: 42,
                    StartedAtUtc: DateTimeOffset.UnixEpoch, Running: true, Displays: [display]), default);
                var key = new Axiom.Domain.ResourceKey("display.hdr", target,
                    Axiom.Domain.ResourceScope.Session("synthetic-owner", System.Diagnostics.Process.GetCurrentProcess().SessionId));
                var result = await runtime.ApplyDesiredAsync(new(key, Axiom.Domain.StateValue.From(new("axiom.bool", 1), true), "synthetic-owner", 0, 1, "synthetic"), cancel.Token);
                Assert.Equal(Axiom.Domain.OutcomeStatus.Succeeded, result.Status);
                if (mode != "disconnect")
                {
                    if (mode == "external") display = display with { Enabled = false, Active = false };
                    if (mode != "exit") await runtime.ProcessEventAsync(Exit(), default);
                    var restored = await runtime.ApplyDesiredAsync(new(key, Axiom.Domain.StateValue.From(new("axiom.bool", 1), false), "synthetic-owner", 0, 2, "restore"), default);
                    Assert.Equal(mode == "external" ? Axiom.Domain.OutcomeStatus.Rejected : Axiom.Domain.OutcomeStatus.Succeeded, restored.Status);
                    Assert.False(display.Enabled);
                    Assert.Equal(mode == "external" ? "已让出控制" : "已恢复", (await runtime.StatusAsync(default)).State);
                }
                Assert.Equal(mode is "disconnect" or "external" ? 1 : 2, applies);
                await runtime.EnsureSafeToStopAsync(default);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }

    [Fact(Timeout = 10000)]
    public async Task CurrentHdrReadReplacesHistoricalSuccessWithoutApplyingOrRecapturing()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-hdr-current-" + Guid.NewGuid().ToString("N") + ".db");
        var target = HdrDisplayTargets.Named("Synthetic HDR");
        var display = new GameHdrDisplayDto("Synthetic HDR", target, true, false, false, null, "synthetic-connection");
        var generation = ""; var applies = 0; var reads = 0; var failRead = false;
        Task<GameHdrMessage> Request(GameHdrMessage request, CancellationToken token)
        {
            if (request.Type == "watch") generation = request.RequestId;
            if (request.Type == "displays")
            {
                reads++;
                if (failRead) throw new IOException("synthetic unavailable");
            }
            if (request.Type == "apply") { applies++; display = display with { Enabled = request.Enabled, Active = request.Enabled }; }
            return Task.FromResult(new GameHdrMessage(1, request.Type == "apply" ? "applied" : request.Type, request.RequestId, Displays: [display]));
        }
        try
        {
            using var store = new SqliteGameHdrStore(path);
            var saved = await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "synthetic", "synthetic.exe", target, true, false));
            var clock = new FakeClock();
            using var engine = new EngineHost(clock, new FakeScheduler(clock), new FakeExecutor()); engine.Start();
            using var runtime = new Axiom.ServiceHost.GameHdrRuntime(engine, store, "synthetic-owner", Request);
            await runtime.InitializeAsync(); await runtime.RefreshAgentAsync(default);
            await runtime.ProcessEventAsync(new(1, "event", "snapshot", Generation: generation, Reason: "snapshot-complete", Displays: [display]), default);
            await runtime.ProcessEventAsync(new(1, "event", "start", Generation: generation, ProcessName: "synthetic.exe", ProcessId: 42,
                StartedAtUtc: DateTimeOffset.UnixEpoch, Running: true, Displays: [display]), default);
            var key = new Axiom.Domain.ResourceKey("display.hdr", target,
                Axiom.Domain.ResourceScope.Session("synthetic-owner", System.Diagnostics.Process.GetCurrentProcess().SessionId));
            var outcome = await runtime.ApplyDesiredAsync(new(key, Axiom.Domain.StateValue.From(new("axiom.bool", 1), true), "synthetic-owner",
                0, 1, "synthetic-current-generation"), default);
            Assert.Equal(Axiom.Domain.OutcomeStatus.Succeeded, outcome.Status);
            Assert.Equal("已切换", (await runtime.StatusAsync(default)).State);
            display = display with { Active = false };
            var inactive = await runtime.StatusAsync(default);
            Assert.Equal("HDR 未生效", inactive.State);
            Assert.False(Assert.Single(inactive.Displays).Active);
            Assert.Contains("实际未生效", GameHdrTaskPresentation.DisplayState(true, false, true));
            display = display with { Enabled = false };
            Assert.Equal("HDR 状态已变化", (await runtime.StatusAsync(default)).State);
            failRead = true;
            var unknown = await runtime.StatusAsync(default);
            Assert.Equal("HDR 状态未确认", unknown.State); Assert.Empty(unknown.Displays);
            Assert.Equal(1, applies); Assert.Equal(5, reads);
            Assert.Equal(saved, Assert.Single(await store.ListAsync()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact(Timeout = 10000)]
    public async Task DisplayMigrationWithUnchangedAndMissingTargetsSurvivesReconnectAndRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-hdr-restart-" + Guid.NewGuid().ToString("N") + ".db");
        const string legacy = @"\\?\DISPLAY#SYN1234#old-instance#{synthetic}";
        var named = HdrDisplayTargets.Named("Synthetic HDR");
        var latest = HdrDisplayTargets.NameSelections([new GameHdrDisplayDto("Synthetic HDR",
            @"\\?\DISPLAY#SYN1234#new-instance#{synthetic}", true, false, false, null)]);
        try
        {
            using var store = new SqliteGameHdrStore(path);
            foreach (var target in new[] { legacy, named, "missing-display" })
                await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "synthetic", "synthetic.exe", target, true, false));
            var originals = await store.ListAsync();
            for (var start = 0; start < 2; start++)
            {
                var clock = new FakeClock();
                using var engine = new EngineHost(clock, new FakeScheduler(clock), new FakeExecutor());
                engine.Start();
                using var runtime = new Axiom.ServiceHost.GameHdrRuntime(engine, store, "synthetic-owner");
                await runtime.InitializeAsync();
                var normalize = typeof(Axiom.ServiceHost.GameHdrRuntime).GetMethod("NormalizeDisplaySelectionsAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                for (var reconnect = 0; reconnect < 2; reconnect++)
                {
                    await (Task)normalize.Invoke(runtime, [latest, CancellationToken.None])!;
                    var current = (await runtime.StatusAsync(default)).Rules;
                    foreach (var original in originals)
                    {
                        var saved = Assert.Single(current, rule => rule.Id == original.Id);
                        var migrated = original.DisplayPath == legacy;
                        Assert.Equal(migrated ? named : original.DisplayPath, saved.DisplayPath);
                        Assert.Equal(original.Version + (migrated ? 1 : 0), saved.Version);
                        Assert.Equal(saved.Version, engine.GetRuleEvaluation(saved.Id).Version);
                    }
                    Assert.Equal(current, await store.ListAsync());
                }
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

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
