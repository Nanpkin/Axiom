using Axiom.Engine;
using Microsoft.Data.Sqlite;

namespace Axiom.Infrastructure;

/// <summary>Single host-owned writer for non-executable rule drafts.</summary>
public sealed class SqliteRuleDraftStore : IRuleDraftStore, IDisposable
{
    private const int SchemaVersion = 2;
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim writer = new(1, 1);

    public SqliteRuleDraftStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Database path is required.", nameof(path));
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existed = File.Exists(path) && new FileInfo(path).Length > 0;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            DefaultTimeout = 2
        };
        connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            Execute("PRAGMA busy_timeout=2000; PRAGMA synchronous=FULL;");
            Initialize(path, existed);
        }
        catch { connection.Dispose(); throw; }
    }

    private void Initialize(string path, bool existed)
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"));
        if (version > SchemaVersion) throw new InvalidDataException($"Unsupported newer rule database schema {version}.");
        if (version == 0 && existed) throw new InvalidDataException("Unversioned rule database rejected; restore a known backup.");
        if (version == 1)
        {
            using var migration = connection.BeginTransaction();
            Execute("ALTER TABLE rule_drafts ADD COLUMN updated_at_utc TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00';", migration);
            Execute("PRAGMA user_version=2;", migration);
            migration.Commit();
        }
        else if (version == 0)
        {
            using var migration = connection.BeginTransaction();
            Execute("CREATE TABLE rule_drafts (id TEXT PRIMARY KEY NOT NULL, version INTEGER NOT NULL CHECK(version > 0), title TEXT NOT NULL, condition_text TEXT NOT NULL, action_text TEXT NOT NULL, updated_at_utc TEXT NOT NULL);", migration);
            Execute("PRAGMA user_version=2;", migration);
            migration.Commit();
        }
        if (!string.Equals(Convert.ToString(Scalar("PRAGMA integrity_check;")), "ok", StringComparison.Ordinal))
            throw new InvalidDataException("Rule database integrity check failed.");
    }

    public async Task<IReadOnlyList<RuleDraft>> ListAsync(CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, version, title, condition_text, action_text, updated_at_utc FROM rule_drafts ORDER BY updated_at_utc DESC, id ASC;";
            using var reader = command.ExecuteReader();
            var result = new List<RuleDraft>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
            }
            return result;
        }
        finally { writer.Release(); }
    }

    public async Task<RuleDraftSaveResult> SaveAsync(RuleDraftEdit edit, DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = connection.BeginTransaction();
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT version FROM rule_drafts WHERE id=$id;";
            read.Parameters.AddWithValue("$id", edit.Id);
            var current = read.ExecuteScalar();
            var currentVersion = current is null ? 0L : Convert.ToInt64(current);
            if (currentVersion != edit.ExpectedVersion)
                return new(RuleDraftSaveStatus.Conflict, null, currentVersion);
            if (currentVersion == 0)
            {
                using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM rule_drafts;";
                if (Convert.ToInt64(count.ExecuteScalar()) >= 64)
                    throw new InvalidOperationException("Rule draft capacity of 64 reached.");
            }

            var next = checked(edit.ExpectedVersion + 1);
            using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = currentVersion == 0
                ? "INSERT INTO rule_drafts (id, version, title, condition_text, action_text, updated_at_utc) VALUES ($id, $next, $title, $condition, $action, $time);"
                : "UPDATE rule_drafts SET version=$next, title=$title, condition_text=$condition, action_text=$action, updated_at_utc=$time WHERE id=$id AND version=$expected;";
            write.Parameters.AddWithValue("$id", edit.Id);
            write.Parameters.AddWithValue("$next", next);
            write.Parameters.AddWithValue("$title", edit.Title);
            write.Parameters.AddWithValue("$condition", edit.ConditionText);
            write.Parameters.AddWithValue("$action", edit.ActionText);
            write.Parameters.AddWithValue("$time", updatedAtUtc.ToUniversalTime().ToString("O"));
            if (currentVersion != 0) write.Parameters.AddWithValue("$expected", edit.ExpectedVersion);
            if (write.ExecuteNonQuery() != 1) throw new IOException("Rule draft write failed before commit.");
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            var saved = new RuleDraft(edit.Id, next, edit.Title, edit.ConditionText, edit.ActionText, updatedAtUtc);
            return new(RuleDraftSaveStatus.Saved, saved, null);
        }
        finally { writer.Release(); }
    }

    private object? Scalar(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void Execute(string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        connection.Dispose();
        writer.Dispose();
    }
}
