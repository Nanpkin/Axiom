using Axiom.Contracts;
using Microsoft.Data.Sqlite;

namespace Axiom.Infrastructure;

public sealed class SqliteGameHdrStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SqliteGameHdrStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 2 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=2000; PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 2) throw new InvalidDataException("Game HDR database schema is newer than this build.");
        command.CommandText = "CREATE TABLE IF NOT EXISTS game_hdr_rules (id TEXT PRIMARY KEY, version INTEGER NOT NULL, title TEXT NOT NULL, process_name TEXT NOT NULL, display_path TEXT NOT NULL, hdr_enabled INTEGER NOT NULL, paused INTEGER NOT NULL, process_names TEXT);";
        command.ExecuteNonQuery();
        if (version == 1)
        {
            using var migration = connection.BeginTransaction();
            command.Transaction = migration;
            command.CommandText = "ALTER TABLE game_hdr_rules ADD COLUMN process_names TEXT;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version=2;";
            command.ExecuteNonQuery();
            migration.Commit();
            command.Transaction = null;
        }
        if (version == 0) { command.CommandText = "PRAGMA user_version=2;"; command.ExecuteNonQuery(); }
        command.CommandText = "PRAGMA integrity_check;";
        if (Convert.ToString(command.ExecuteScalar()) != "ok") throw new InvalidDataException("Game HDR database integrity check failed.");
    }

    public async Task<GameHdrRuleDto[]> ListAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, version, title, process_name, display_path, hdr_enabled, paused, process_names FROM game_hdr_rules ORDER BY title, id;";
            using var reader = command.ExecuteReader();
            var result = new List<GameHdrRuleDto>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt64(5) != 0, reader.GetInt64(6) != 0,
                reader.IsDBNull(7) ? null : reader.GetString(7)));
            return [.. result];
        }
        finally { gate.Release(); }
    }

    public async Task<GameHdrRuleDto> SaveAsync(GameHdrRuleDto rule, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var transaction = connection.BeginTransaction();
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT version FROM game_hdr_rules WHERE id=$id;";
            read.Parameters.AddWithValue("$id", rule.Id);
            var current = read.ExecuteScalar();
            var version = current is null ? 0L : Convert.ToInt64(current);
            if (version != rule.Version) throw new InvalidOperationException("规则已在别处更改，请重新读取。 ");
            if (version == 0)
            {
                using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM game_hdr_rules;";
                if (Convert.ToInt64(count.ExecuteScalar()) >= 32) throw new InvalidOperationException("最多保存 32 条游戏规则。 ");
            }
            var saved = rule with { Version = checked(version + 1) };
            using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "INSERT INTO game_hdr_rules (id,version,title,process_name,display_path,hdr_enabled,paused,process_names) VALUES ($id,$version,$title,$name,$path,$hdr,$paused,$names) ON CONFLICT(id) DO UPDATE SET version=$version,title=$title,process_name=$name,display_path=$path,hdr_enabled=$hdr,paused=$paused,process_names=$names;";
            write.Parameters.AddWithValue("$id", saved.Id);
            write.Parameters.AddWithValue("$version", saved.Version);
            write.Parameters.AddWithValue("$title", saved.Title);
            write.Parameters.AddWithValue("$name", saved.ProcessName);
            write.Parameters.AddWithValue("$names", (object?)saved.ProcessNamesText ?? DBNull.Value);
            write.Parameters.AddWithValue("$path", saved.DisplayPath);
            write.Parameters.AddWithValue("$hdr", saved.HdrEnabled ? 1 : 0);
            write.Parameters.AddWithValue("$paused", saved.Paused ? 1 : 0);
            write.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return saved;
        }
        finally { gate.Release(); }
    }

    public async Task DeleteAsync(string id, long version, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM game_hdr_rules WHERE id=$id AND version=$version;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", version);
            token.ThrowIfCancellationRequested();
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("规则已在别处更改，请重新读取。 ");
        }
        finally { gate.Release(); }
    }

    public void Dispose() { connection.Dispose(); gate.Dispose(); }
}
