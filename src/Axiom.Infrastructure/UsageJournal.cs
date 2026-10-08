using System.Text.Json;
using Axiom.Contracts;
using Microsoft.Data.Sqlite;

namespace Axiom.Infrastructure;

// All mutation is serialized by the host controller; storage uses no background polling.
public sealed class UsageJournal : IDisposable
{
    private readonly SqliteConnection connection;
    public UsageJournal(string path)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA secure_delete=ON; CREATE TABLE IF NOT EXISTS usage_sessions(id TEXT PRIMARY KEY, started TEXT NOT NULL, body TEXT NOT NULL); CREATE TABLE IF NOT EXISTS usage_feedback(session TEXT NOT NULL, id TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY(session,id));";
        command.ExecuteNonQuery();
        foreach (var session in Read(DateTimeOffset.UtcNow).Where(s => s.State is "recording" or "paused"))
            Save(session with { State = "stopped", Gaps = [.. session.Gaps.TakeLast(31), "后台曾停止；没有自动恢复观察。"] });
    }
    public UsageSession[] Read(DateTimeOffset now)
    {
        using (var prune = connection.CreateCommand())
        {
            prune.CommandText = "DELETE FROM usage_sessions WHERE started < $cutoff; DELETE FROM usage_feedback WHERE session NOT IN (SELECT id FROM usage_sessions);";
            prune.Parameters.AddWithValue("$cutoff", now.AddDays(-7).ToString("O")); prune.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand(); command.CommandText = "SELECT body FROM usage_sessions ORDER BY started DESC LIMIT 12";
        using var reader = command.ExecuteReader(); var sessions = new List<UsageSession>();
        while (reader.Read()) sessions.Add(JsonSerializer.Deserialize<UsageSession>(reader.GetString(0)) ?? throw new InvalidDataException("观察记录损坏。"));
        return [.. sessions];
    }
    public void Save(UsageSession session)
    {
        var body = JsonSerializer.Serialize(session);
        if (System.Text.Encoding.UTF8.GetByteCount(body) > 1024 * 1024) throw new InvalidDataException("观察记录达到容量上限。");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO usage_sessions VALUES($id,$at,$body) ON CONFLICT(id) DO UPDATE SET body=$body; DELETE FROM usage_sessions WHERE id NOT IN (SELECT id FROM usage_sessions ORDER BY started DESC LIMIT 12); DELETE FROM usage_feedback WHERE session NOT IN (SELECT id FROM usage_sessions);";
        command.Parameters.AddWithValue("$id", session.Id); command.Parameters.AddWithValue("$at", session.StartedUtc.ToString("O")); command.Parameters.AddWithValue("$body", body); command.ExecuteNonQuery();
    }
    public void Delete(string? id)
    {
        using var command = connection.CreateCommand(); command.CommandText = id is null ? "DELETE FROM usage_sessions; DELETE FROM usage_feedback; VACUUM;" : "DELETE FROM usage_sessions WHERE id=$id; DELETE FROM usage_feedback WHERE session=$id; VACUUM;";
        if (id is not null) command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }
    public void Feedback(string session, string id, string value)
    {
        using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO usage_feedback VALUES($s,$id,$v) ON CONFLICT(session,id) DO UPDATE SET value=$v; DELETE FROM usage_feedback WHERE rowid NOT IN (SELECT rowid FROM usage_feedback ORDER BY rowid DESC LIMIT 256);";
        command.Parameters.AddWithValue("$s", session); command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$v", value); command.ExecuteNonQuery();
    }
    public HashSet<string> Dismissed(string session)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT id FROM usage_feedback WHERE session=$s"; command.Parameters.AddWithValue("$s", session);
        using var reader = command.ExecuteReader(); var ids = new HashSet<string>(); while (reader.Read()) ids.Add(reader.GetString(0)); return ids;
    }
    public void Dispose() => connection.Dispose();
}

public sealed class UsageRecorder
{
    public UsageSession Session { get; private set; }
    public UsageRecorder(UsageOptions options, DateTimeOffset now)
    {
        UsageValidation.Options(options);
        Session = new(Guid.NewGuid().ToString("D"), now, now.AddMinutes(options.Minutes), "recording", options, [], [], []);
    }
    public void State(string state, string? gap = null)
    {
        if (gap is not null && Session.Events.Length < UsageValidation.MaximumEvents)
            Session = Session with { Events = [.. Session.Events, new(DateTimeOffset.UtcNow, "gap", null, "unknown")] };
        Session = Session with { State = state, Gaps = gap is null ? Session.Gaps : [.. Session.Gaps.TakeLast(31), gap] };
    }
    public bool Add(UsageSample sample)
    {
        if (Session.State != "recording") return false;
        if (sample.AtUtc >= Session.EndsUtc) { State("stopped"); return false; }
        if (sample.AtUtc < Session.StartedUtc || sample.Kind is not ("foreground" or "process-start" or "axiom-action" or "gap") || sample.Origin is not ("unknown" or "axiom")) throw new ArgumentException("观察事件无效。");
        if (sample.Kind == "gap") { State(Session.Events.Length >= UsageValidation.MaximumEvents - 1 ? "stopped" : "recording", "事件无法识别或队列溢出；这一段数据不完整。"); return true; }
        if (sample.Kind == "foreground" && !Session.Options.Foreground || sample.Kind == "process-start" && !Session.Options.ProcessStarts) return false;
        string? id = null;
        if (sample.Executable is { } path)
        {
            if (path.Length > 1024 || !Path.IsPathFullyQualified(path)) { State("recording", "程序身份无法确认。"); return false; }
            if (Session.Options.ExcludedApplications.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return false;
            var known = Session.Objects.FirstOrDefault(o => o.Executable.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                if (Session.Objects.Length >= UsageValidation.MaximumObjects || Session.Objects.Sum(o => o.Executable.Length) + path.Length > 12000) { State("stopped", "对象数量或字典容量达到上限，观察已结束。"); return false; }
                known = new("app-" + Guid.NewGuid().ToString("N")[..12], path); Session = Session with { Objects = [.. Session.Objects, known] };
            }
            id = known.Id;
        }
        var last = Session.Events.LastOrDefault();
        if (last is not null && last.Kind == sample.Kind && last.ObjectId == id && sample.Kind == "foreground") return false;
        if (Session.Events.Length >= UsageValidation.MaximumEvents) { State("stopped", "达到 4096 条事件上限，观察已结束。"); return false; }
        Session = Session with { Events = [.. Session.Events, new(sample.AtUtc, sample.Kind, id, sample.Origin)] }; return true;
    }
}
