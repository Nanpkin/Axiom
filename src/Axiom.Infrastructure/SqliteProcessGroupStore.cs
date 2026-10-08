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
        if (Convert.ToInt32(command.ExecuteScalar()) > 2) throw new InvalidDataException("进程组数据版本较新，请更新 Axiom。");
        using (var transaction = connection.BeginTransaction())
        {
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE IF NOT EXISTS process_groups(id TEXT PRIMARY KEY, version INTEGER NOT NULL, name TEXT NOT NULL, entries TEXT NOT NULL); CREATE TABLE IF NOT EXISTS steam_tool(id INTEGER PRIMARY KEY CHECK(id=1), state TEXT NOT NULL); PRAGMA user_version=2;";
            command.ExecuteNonQuery(); transaction.Commit(); command.Transaction = null;
        }
        command.CommandText = "PRAGMA integrity_check;";
        if (Convert.ToString(command.ExecuteScalar()) != "ok") throw new InvalidDataException("进程组数据校验失败。");
    }
    public ProcessGroup[] Read() => Read(null);
    private ProcessGroup[] Read(SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT id, version, name, entries FROM process_groups ORDER BY rowid;";
        using var reader = command.ExecuteReader(); var groups = new List<ProcessGroup>();
        while (reader.Read()) groups.Add(ProcessGroups.Normalize(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
            JsonSerializer.Deserialize<ProcessGroupEntry[]>(reader.GetString(3)) ?? throw new InvalidDataException("进程组内容无效。"))));
        return [.. groups];
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
        var updated = all.Where(x => x.Id != group.Id).Append(group).ToArray();
        if (updated.Length > ProcessGroups.MaximumGroups || updated.Sum(x => x.Entries.Length) > ProcessGroups.MaximumTotalEntries)
            throw new InvalidOperationException("最多保存 32 个组、合计 512 个程序，请先清理不需要的组。");
        if (JsonSerializer.SerializeToUtf8Bytes(updated).Length > AutomationProtocol.MaximumFrame - 8192)
            throw new InvalidOperationException("进程组内容过大，请缩短名称或清理不需要的组。");
        if (updated.Where(x => x.Id != group.Id).Any(x => string.Equals(x.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("已有同名组，请换一个名称。");
        var saved = group with { Version = checked(group.Version + 1) };
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO process_groups VALUES($id,$version,$name,$entries) ON CONFLICT(id) DO UPDATE SET version=excluded.version,name=excluded.name,entries=excluded.entries;";
        command.Parameters.AddWithValue("$id", saved.Id); command.Parameters.AddWithValue("$version", saved.Version);
        command.Parameters.AddWithValue("$name", saved.Name); command.Parameters.AddWithValue("$entries", JsonSerializer.Serialize(saved.Entries));
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
        using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM process_groups WHERE id=$id AND version=$version;";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$version", version);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("这个组已在别处更改，请刷新后重新编辑。");
    }
    public void Dispose() => connection.Dispose();
}
