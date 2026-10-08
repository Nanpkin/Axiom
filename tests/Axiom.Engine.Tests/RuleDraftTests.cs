using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Axiom.Engine.Tests;

public sealed class RuleDraftTests
{
    private const string Owner = "S-1-5-21-123-456-789-1001";
    private static readonly string Id = Guid.Parse("64b3ab0c-15b5-4bd8-9473-21cba54bbbf0").ToString("D");
    private static RuleDraftEdit Edit(long version, string title = "锁屏时调整显示") =>
        new(Id, version, title, "当用户锁屏时", "将显示亮度设为低值");

    [Fact]
    public async Task SaveCommitsBeforeAckAndReopenedStoreReadsLatestVersion()
    {
        using var temp = new TempDatabase();
        using (var store = new SqliteRuleDraftStore(temp.Path))
        using (var fixture = new Fixture())
        {
            fixture.Engine.ConfigureRuleDrafts(store, Owner);
            var first = await fixture.Engine.SaveRuleDraftAsync(Owner, Edit(0));
            Assert.Equal(RuleDraftSaveStatus.Saved, first.Status);
            Assert.Equal(1, first.Draft!.Version);
            Assert.Equal(fixture.Clock.UtcNow, first.Draft.UpdatedAtUtc);
            var changed = await fixture.Engine.SaveRuleDraftAsync(Owner, Edit(1, "新名称"));
            Assert.Equal(2, changed.Draft!.Version);
        }
        using (var reopened = new SqliteRuleDraftStore(temp.Path))
        {
            var item = Assert.Single(await reopened.ListAsync(default));
            Assert.Equal(2, item.Version);
            Assert.Equal("新名称", item.Title);
            Assert.Equal("当用户锁屏时", item.ConditionText);
        }
    }

    [Fact]
    public async Task StaleVersionConflictsWithoutOverwritingCommittedValue()
    {
        using var temp = new TempDatabase();
        using var store = new SqliteRuleDraftStore(temp.Path);
        using var fixture = new Fixture();
        fixture.Engine.ConfigureRuleDrafts(store, Owner);
        await fixture.Engine.SaveRuleDraftAsync(Owner, Edit(0));
        var conflict = await fixture.Engine.SaveRuleDraftAsync(Owner, Edit(0, "不应覆盖"));
        Assert.Equal(RuleDraftSaveStatus.Conflict, conflict.Status);
        Assert.Equal(1, conflict.CurrentVersion);
        Assert.Null(conflict.Draft);
        Assert.Equal("锁屏时调整显示", Assert.Single(await fixture.Engine.ListRuleDraftsAsync(Owner)).Title);
    }

    [Fact]
    public async Task SqliteWriteFailureCannotProduceSavedResult()
    {
        using var temp = new TempDatabase();
        using var store = new SqliteRuleDraftStore(temp.Path);
        using var fixture = new Fixture();
        fixture.Engine.ConfigureRuleDrafts(store, Owner);
        await fixture.Engine.SaveRuleDraftAsync(Owner, Edit(0));
        using (var blocker = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            blocker.Open();
            using var lockTransaction = blocker.BeginTransaction();
            using var lockCommand = blocker.CreateCommand();
            lockCommand.Transaction = lockTransaction;
            lockCommand.CommandText = "UPDATE rule_drafts SET title='held' WHERE id=$id;";
            lockCommand.Parameters.AddWithValue("$id", Id);
            Assert.Equal(1, lockCommand.ExecuteNonQuery());
            await Assert.ThrowsAsync<SqliteException>(() => fixture.Engine.SaveRuleDraftAsync(Owner, Edit(1, "不得确认")));
            lockTransaction.Rollback();
        }
        Assert.Equal("锁屏时调整显示", Assert.Single(await store.ListAsync(default)).Title);
    }

    [Fact]
    public async Task MigrationUpdatesV1InPlaceAndRetainsDraftWhileFutureVersionFailsClosed()
    {
        using var temp = new TempDatabase();
        using (var connection = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE rule_drafts (id TEXT PRIMARY KEY NOT NULL, version INTEGER NOT NULL, title TEXT NOT NULL, condition_text TEXT NOT NULL, action_text TEXT NOT NULL);" +
                $"INSERT INTO rule_drafts VALUES ('{Id}',1,'旧草稿','条件','动作'); PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        using (var store = new SqliteRuleDraftStore(temp.Path))
        {
            Assert.Equal("旧草稿", Assert.Single(await store.ListAsync(default)).Title);
            Assert.Equal(2, (await store.SaveAsync(Edit(1, "迁移后编辑"), DateTimeOffset.UnixEpoch, default)).Draft!.Version);
        }
        Assert.False(File.Exists(temp.Path + ".pre-v2.bak"));
        using (var migrated = new SqliteConnection($"Data Source={temp.Path};Mode=ReadOnly;Pooling=False"))
        {
            migrated.Open();
            using var version = migrated.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(2L, (long)version.ExecuteScalar()!);
        }
        using (var connection = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => new SqliteRuleDraftStore(temp.Path));
    }

    [Fact]
    public async Task EngineRejectsForeignPrincipalAndInvalidDraftBeforeStore()
    {
        using var fixture = new Fixture();
        var fake = new CountingStore();
        fixture.Engine.ConfigureRuleDrafts(fake, Owner);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Engine.ListRuleDraftsAsync("another-sid"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Engine.SaveRuleDraftAsync("another-sid", Edit(0)));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Engine.SaveRuleDraftAsync(Owner, Edit(0) with { Title = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Engine.SaveRuleDraftAsync(Owner, Edit(0) with { ConditionText = new string('x', 2001) }));
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task StoreFailurePropagatesAndDoesNotBecomeSaved()
    {
        using var fixture = new Fixture();
        fixture.Engine.ConfigureRuleDrafts(new FailingStore(), Owner);
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.SaveRuleDraftAsync(Owner, Edit(0)));
    }

    [Fact]
    public async Task ProtocolRejectsScopePayloadAndOversizedFrame()
    {
        var request = new RuleDraftMessage(1, 0, 1, "save", "r1", RuleDraftProtocol.Scope,
            Id, 0, "名称", "条件", "动作", DateTimeOffset.UtcNow.AddSeconds(10));
        RuleDraftProtocol.ValidateRequest(request, DateTimeOffset.UtcNow);
        Assert.Throws<UnauthorizedAccessException>(() => RuleDraftProtocol.ValidateRequest(request with { Scope = "machine" }, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => RuleDraftProtocol.ValidateRequest(request with { Draft = new(Id, 1, "x", "x", "x", DateTimeOffset.UtcNow) }, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => RuleDraftProtocol.ValidateRequest(request with { Type = "execute" }, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => RuleDraftProtocol.ValidateRequest(request with { DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, DateTimeOffset.UtcNow));
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, RuleDraftProtocol.MaximumFrame + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuleDraftProtocol.ReadAsync(new MemoryStream(prefix), default));

        var forged = Encoding.UTF8.GetBytes("{\"Major\":1,\"Minor\":0,\"Schema\":1,\"Type\":\"list\",\"RequestId\":\"forged\",\"Scope\":\"user\",\"CallerSid\":\"S-1-5-18\"}");
        BinaryPrimitives.WriteInt32LittleEndian(prefix, forged.Length);
        await using var frame = new MemoryStream();
        await frame.WriteAsync(prefix);
        await frame.WriteAsync(forged);
        frame.Position = 0;
        await Assert.ThrowsAsync<JsonException>(() => RuleDraftProtocol.ReadAsync(frame, default));
    }

    private sealed class CountingStore : IRuleDraftStore
    {
        public int Calls;
        public Task<IReadOnlyList<RuleDraft>> ListAsync(CancellationToken _) { Calls++; return Task.FromResult<IReadOnlyList<RuleDraft>>([]); }
        public Task<RuleDraftSaveResult> SaveAsync(RuleDraftEdit _, DateTimeOffset __, CancellationToken ___)
        { Calls++; return Task.FromResult(new RuleDraftSaveResult(RuleDraftSaveStatus.Saved, null, null)); }
    }

    private sealed class FailingStore : IRuleDraftStore
    {
        public Task<IReadOnlyList<RuleDraft>> ListAsync(CancellationToken _) => Task.FromResult<IReadOnlyList<RuleDraft>>([]);
        public Task<RuleDraftSaveResult> SaveAsync(RuleDraftEdit _, DateTimeOffset __, CancellationToken ___) =>
            Task.FromException<RuleDraftSaveResult>(new IOException("disk failure"));
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AxiomRuleTests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "drafts.db");
        public TempDatabase() => Directory.CreateDirectory(directory);
        public void Dispose()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AxiomRuleTests")) + System.IO.Path.DirectorySeparatorChar;
            var target = System.IO.Path.GetFullPath(directory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
