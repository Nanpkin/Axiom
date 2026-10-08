using Axiom.Contracts;
using Microsoft.Data.Sqlite;

namespace Axiom.Infrastructure;

public sealed class SqliteShortcutStore : IShortcutStore, IDisposable
{
    private readonly SqliteConnection connection;
    public SqliteShortcutStore(string path)
    {
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 2 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=2000; PRAGMA synchronous=FULL;"; command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 1) throw new InvalidDataException("快捷键数据库版本较新，请更新 Axiom。");
        command.CommandText = "CREATE TABLE IF NOT EXISTS shortcuts (id TEXT PRIMARY KEY, version INTEGER NOT NULL, function_id TEXT NOT NULL, modifiers INTEGER NOT NULL, key_name TEXT NOT NULL, enabled INTEGER NOT NULL);";
        command.ExecuteNonQuery();
        if (version == 0)
        {
            using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO shortcuts VALUES ($id, 1, 'app.show-window', 3, 'A', 1); PRAGMA user_version=1;";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D")); command.ExecuteNonQuery(); transaction.Commit();
            command.Transaction = null; command.Parameters.Clear();
        }
        command.CommandText = "PRAGMA integrity_check;";
        if (Convert.ToString(command.ExecuteScalar()) != "ok") throw new InvalidDataException("快捷键数据库校验失败。");
        KeyboardShortcuts.ValidateSet(Read());
    }
    public ShortcutBinding[] Read() => Read(null);
    private ShortcutBinding[] Read(SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT id, version, function_id, modifiers, key_name, enabled FROM shortcuts ORDER BY rowid;";
        using var reader = command.ExecuteReader(); var items = new List<ShortcutBinding>();
        while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), new((ShortcutModifiers)reader.GetInt32(3), reader.GetString(4)), reader.GetBoolean(5)));
        return [.. items];
    }
    public ShortcutBinding Save(ShortcutBinding binding)
    {
        KeyboardShortcuts.Validate(binding);
        using var transaction = connection.BeginTransaction();
        var current = Read(transaction); var existing = current.SingleOrDefault(x => x.Id == binding.Id);
        if ((existing?.Version ?? 0) != binding.Version) throw new InvalidOperationException("快捷键已在别处更改，请刷新后重新编辑。");
        var saved = binding with { Version = checked(binding.Version + 1) };
        KeyboardShortcuts.ValidateSet(current.Where(x => x.Id != saved.Id).Append(saved));
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO shortcuts VALUES ($id, $version, $function, $modifiers, $key, $enabled) ON CONFLICT(id) DO UPDATE SET version=excluded.version, function_id=excluded.function_id, modifiers=excluded.modifiers, key_name=excluded.key_name, enabled=excluded.enabled;";
        command.Parameters.AddWithValue("$id", saved.Id); command.Parameters.AddWithValue("$version", saved.Version);
        command.Parameters.AddWithValue("$function", saved.FunctionId); command.Parameters.AddWithValue("$modifiers", (int)saved.Chord.Modifiers);
        command.Parameters.AddWithValue("$key", saved.Chord.Key); command.Parameters.AddWithValue("$enabled", saved.Enabled);
        command.ExecuteNonQuery(); transaction.Commit(); return saved;
    }
    public void Delete(string id, long version)
    {
        using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM shortcuts WHERE id=$id AND version=$version;";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$version", version);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("快捷键已在别处更改，请刷新后重新编辑。");
    }
    public void Dispose() => connection.Dispose();
}
