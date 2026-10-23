using System.Text.Json;
using Axiom.Contracts;
using Microsoft.Data.Sqlite;

namespace Axiom.Infrastructure;

public sealed class SqliteProcessGroupStore : IDisposable
{
    private readonly SqliteConnection connection;
    public SqliteProcessGroupStore(string path)
    {
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 2 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=2000; PRAGMA synchronous=FULL;"; command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version;";
        var schemaVersion = Convert.ToInt32(command.ExecuteScalar());
        if (schemaVersion > 3) throw new InvalidDataException("进程组数据版本较新，请更新 Axiom。");
        using (var transaction = connection.BeginTransaction())
        {
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE IF NOT EXISTS process_groups(id TEXT PRIMARY KEY, version INTEGER NOT NULL, name TEXT NOT NULL, entries TEXT NOT NULL); CREATE TABLE IF NOT EXISTS steam_tool(id INTEGER PRIMARY KEY CHECK(id=1), state TEXT NOT NULL);";
            command.ExecuteNonQuery();
            if (schemaVersion < 3)
            {
                command.CommandText = "ALTER TABLE process_groups ADD COLUMN included_groups TEXT NOT NULL DEFAULT '[]';";
                command.ExecuteNonQuery();
            }
            command.CommandText = "PRAGMA user_version=3;";
            command.ExecuteNonQuery(); transaction.Commit(); command.Transaction = null;
        }
        command.CommandText = "PRAGMA integrity_check;";
        if (Convert.ToString(command.ExecuteScalar()) != "ok") throw new InvalidDataException("进程组数据校验失败。");
    }
    public ProcessGroup[] Read() => Read(null);
    private ProcessGroup[] Read(SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT id, version, name, entries, included_groups FROM process_groups ORDER BY rowid;";
        using var reader = command.ExecuteReader(); var groups = new List<ProcessGroup>();
        while (reader.Read()) groups.Add(ProcessGroups.Normalize(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
            JsonSerializer.Deserialize<ProcessGroupEntry[]>(reader.GetString(3)) ?? throw new InvalidDataException("进程组内容无效。"),
            IncludedGroupIds: JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? throw new InvalidDataException("进程组引用无效。"))));
        return ProcessGroups.Resolve([.. groups]);
    }
    public ProcessGroup Save(ProcessGroup group)
    {
        using var transaction = connection.BeginTransaction();
        var saved = Save(group, transaction); transaction.Commit(); return saved;
    }
    private ProcessGroup Save(ProcessGroup group, SqliteTransaction transaction)
    {
        group = ProcessGroups.Normalize(group);
        var all = Read(transaction); var existing = all.SingleOrDefault(x => x.Id == group.Id);
        if ((existing?.Version ?? 0) != group.Version) throw new InvalidOperationException("这个组已在别处更改，请刷新后重新编辑。");
        var saved = group with { Version = checked(group.Version + 1) };
        var updated = ProcessGroups.Resolve(all.Where(x => x.Id != group.Id).Append(saved).ToArray());
        if (JsonSerializer.SerializeToUtf8Bytes(updated).Length > AutomationProtocol.MaximumFrame - 8192)
            throw new InvalidOperationException("进程组内容过大，请缩短名称或清理不需要的组。");
        if (updated.Where(x => x.Id != group.Id).Any(x => string.Equals(x.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("已有同名组，请换一个名称。");
        saved = updated.Single(x => x.Id == group.Id);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO process_groups(id,version,name,entries,included_groups) VALUES($id,$version,$name,$entries,$references) ON CONFLICT(id) DO UPDATE SET version=excluded.version,name=excluded.name,entries=excluded.entries,included_groups=excluded.included_groups;";
        command.Parameters.AddWithValue("$id", saved.Id); command.Parameters.AddWithValue("$version", saved.Version);
        command.Parameters.AddWithValue("$name", saved.Name); command.Parameters.AddWithValue("$entries", JsonSerializer.Serialize(saved.Entries));
        command.Parameters.AddWithValue("$references", JsonSerializer.Serialize(saved.IncludedGroupIds));
        command.ExecuteNonQuery(); return saved;
    }
    public SteamToolState ReadSteamTool()
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT state FROM steam_tool WHERE id=1;";
        var text = command.ExecuteScalar() as string;
        if (text is null) return SteamToolState.Empty;
        var state = JsonSerializer.Deserialize<SteamToolState>(text) ?? throw new InvalidDataException("Steam 工具设置无效。");
        _ = SteamTool.Normalize(state.Settings);
        if (state.Processes is null || state.Processes.Length > 4096 || state.Processes.Any(e => e is null || !ProcessGroups.ValidProcessName(e.ProcessName) || string.IsNullOrWhiteSpace(e.DisplayName) || e.DisplayName.Length > 120) ||
            state.Warnings is null || state.Warnings.Length > 12 || !Guid.TryParseExact(state.ScanId, "N", out _) || state.GroupId is not null && !Guid.TryParseExact(state.GroupId, "D", out _))
            throw new InvalidDataException("Steam 工具结果无效。");
        return state;
    }
    public void SaveSteamTool(SteamToolState state, ProcessGroup? group = null)
    {
        using var transaction = connection.BeginTransaction();
        if (group is not null) Save(group, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO steam_tool(id,state) VALUES(1,$state) ON CONFLICT(id) DO UPDATE SET state=excluded.state;";
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state)); command.ExecuteNonQuery(); transaction.Commit();
    }
    public void Delete(string id, long version)
    {
        if (!Guid.TryParseExact(id, "D", out var parsedId)) throw new ArgumentException("进程组标识无效。");
        id = parsedId.ToString("D");
        using var transaction = connection.BeginTransaction();
        var all = Read(transaction);
        if (all.SingleOrDefault(g => g.Id == id)?.Version != version)
            throw new InvalidOperationException("这个组已在别处更改，请刷新后重新编辑。");
        if (all.Any(g => g.IncludedGroupIds!.Contains(id)))
            throw new InvalidOperationException("这个组正被其他进程组包含，请先解除包含关系再删除。");
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "DELETE FROM process_groups WHERE id=$id AND version=$version;";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$version", version);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("这个组已在别处更改，请刷新后重新编辑。");
        transaction.Commit();
    }
    public void Dispose() => connection.Dispose();
}
