using Axiom.Domain;
using Microsoft.Data.Sqlite;

namespace Axiom.Infrastructure;

// Called only by ActivityJournal's single consumer.
public sealed class SqliteActivityStore : IDisposable
{
    public const int DefaultPageSize = 30;
    // Kept for callers compiled against the first activity-history preview.
    public const int PageSize = DefaultPageSize;
    public const int MaximumPageSize = 64;
    private readonly SqliteConnection connection;

    public SqliteActivityStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 2 }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=2000; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version > 1) throw new InvalidDataException("活动记录数据库版本较新，无法读取。");
            command.CommandText = "CREATE TABLE IF NOT EXISTS activity_records (id INTEGER PRIMARY KEY AUTOINCREMENT, at_utc INTEGER NOT NULL, category TEXT NOT NULL, title TEXT NOT NULL, detail TEXT NOT NULL, level INTEGER NOT NULL, subject TEXT); CREATE INDEX IF NOT EXISTS activity_by_time ON activity_records(at_utc); PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        catch { connection.Dispose(); throw; }
    }

    public void Append(IReadOnlyList<ActivityEvent> entries)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO activity_records(at_utc,category,title,detail,level,subject) VALUES ($at,$category,$title,$detail,$level,$subject);";
            command.Parameters.AddWithValue("$at", entry.AtUtc.UtcTicks);
            command.Parameters.AddWithValue("$category", entry.Category);
            command.Parameters.AddWithValue("$title", entry.Title);
            command.Parameters.AddWithValue("$detail", entry.Detail);
            command.Parameters.AddWithValue("$level", (int)entry.Level);
            command.Parameters.AddWithValue("$subject", (object?)entry.Subject ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public ActivityPage Read(long? beforeId, int pageSize = DefaultPageSize)
    {
        if (pageSize is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,at_utc,category,title,detail,level,subject FROM activity_records " +
            (beforeId is null ? "" : "WHERE id < $before ") + "ORDER BY id DESC LIMIT $limit;";
        if (beforeId is not null) command.Parameters.AddWithValue("$before", beforeId.Value);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        using var reader = command.ExecuteReader();
        var result = new List<ActivityRecord>();
        while (reader.Read()) result.Add(ReadRecord(reader, summary: true));
        var more = result.Count > pageSize;
        if (more) result.RemoveAt(pageSize);
        return new([.. result], more ? result[^1].Id : null);
    }

    public ActivityRecord? ReadDetail(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,at_utc,category,title,detail,level,subject FROM activity_records WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader, summary: false) : null;
    }

    private static ActivityRecord ReadRecord(SqliteDataReader reader, bool summary)
    {
        var detail = reader.GetString(4);
        var entry = new ActivityEvent(new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2),
            reader.GetString(3), detail, (ActivityLevel)reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6));
        if (!summary) return new(reader.GetInt64(0), entry);
        var preview = ActivityText.Summary(detail);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(detail)));
        return new(reader.GetInt64(0), entry with { Detail = preview }, fingerprint);
    }

    public ActivityCleanupResult ClearBefore(DateTimeOffset beforeUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM activity_records WHERE at_utc < $before;";
        command.Parameters.AddWithValue("$before", beforeUtc.UtcTicks);
        return new(command.ExecuteNonQuery(), beforeUtc);
    }

    public void Dispose() => connection.Dispose();
}
