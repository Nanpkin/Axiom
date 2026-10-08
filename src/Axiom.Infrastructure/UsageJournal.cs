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
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); connection.Open();
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
    public HashSet<string> Dismissed(string? session)
    {
        using var command = connection.CreateCommand(); command.CommandText = session is null ? "SELECT id FROM usage_feedback" : "SELECT id FROM usage_feedback WHERE session=$s"; if (session is not null) command.Parameters.AddWithValue("$s", session);
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
    public void Annotate(string purpose)
    {
        var options = Session.Options with { Purpose = purpose }; UsageValidation.Options(options);
        Session = Session with { Options = options };
    }
    public void State(string state, string? gap = null, DateTimeOffset? atUtc = null)
    {
        var at = atUtc ?? DateTimeOffset.UtcNow;
        if (state is not ("recording" or "paused" or "stopped")) throw new ArgumentException("观察状态无效。");
        if (state is "paused" or "stopped" || gap is not null) CloseDwell(at);
        if (gap is not null && Session.Events.Length < UsageValidation.MaximumEvents)
            Session = Session with { Events = [.. Session.Events, new(at, "gap", null, "unknown")] };
        Session = Session with { State = state, StoppedUtc = state == "stopped" ? Session.StoppedUtc ?? at : Session.StoppedUtc, Gaps = gap is null ? Session.Gaps : [.. Session.Gaps.TakeLast(31), gap] };
    }
    private void CloseDwell(DateTimeOffset at)
    {
        var index = Array.FindLastIndex(Session.Events, e => e.Kind == "foreground" && e.DwellSeconds is null);
        if (index < 0) return;
        var events = Session.Events.ToArray(); var previous = events[index];
        events[index] = previous with { DwellSeconds = Math.Max(0, (at - previous.AtUtc).TotalSeconds) };
        Session = Session with { Events = events };
    }
    public bool Add(UsageSample sample)
    {
        if (Session.State != "recording") return false;
        if (sample.AtUtc >= Session.EndsUtc) { State("stopped", atUtc: Session.EndsUtc); return false; }
        if (sample.AtUtc < Session.StartedUtc) return false;
        if (sample.Kind is not ("foreground" or "foreground-unobserved" or "process-start" or "process-exit" or "window-shown" or "window-hidden" or "window-minimized" or "window-maximized" or "window-normal" or "window-left" or "window-right" or "window-center" or "axiom-action" or "gap" or "system-locked" or "system-unlocked" or "system-resumed" or "system-ac" or "system-battery" or "system-network-up" or "system-network-down") || sample.Origin is not ("unknown" or "axiom")) throw new ArgumentException("观察事件无效。");
        if (sample.Kind == "foreground-unobserved")
        {
            if (!Session.Options.Foreground) return false;
            if (Session.Events.LastOrDefault(e => e.Kind is "foreground" or "gap") is { Kind: "gap" }) return false;
            State(Session.Events.Length >= UsageValidation.MaximumEvents - 1 ? "stopped" : "recording", "前台处于未记录范围，相关停留与序列中断。", sample.AtUtc); return true;
        }
        if (sample.Kind == "gap") { State(Session.Events.Length >= UsageValidation.MaximumEvents - 1 ? "stopped" : "recording", "事件无法识别或队列溢出；这一段数据不完整。", sample.AtUtc); return true; }
        if (sample.Kind == "foreground" && !Session.Options.Foreground || sample.Kind == "process-start" && !Session.Options.ProcessStarts || sample.Kind == "process-exit" && !Session.Options.ProcessExits) return false;
        if (sample.Kind.StartsWith("window-", StringComparison.Ordinal) && !Session.Options.WindowStates) return false;
        if (sample.Kind.StartsWith("system-", StringComparison.Ordinal) && !Session.Options.SystemEvents) return false;
        string? id = null;
        if (sample.Executable is { } path)
        {
            if (path.Length > 1024 || !Path.IsPathFullyQualified(path)) { State("recording", "程序身份无法确认。"); return false; }
            if (Session.Options.ExcludedApplications.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                if (sample.Kind == "foreground" && Session.Events.Any(e => e.Kind == "foreground" && e.DwellSeconds is null))
                    State("recording", "排除应用期间的前台活动未记录，相关序列中断。", sample.AtUtc);
                return false;
            }
            var known = Session.Objects.FirstOrDefault(o => o.Executable.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                if (Session.Objects.Length >= UsageValidation.MaximumObjects || Session.Objects.Sum(o => o.Executable.Length) + path.Length > 12000) { State("stopped", "对象数量或字典容量达到上限，观察已结束。"); return false; }
                known = new("app-" + Guid.NewGuid().ToString("N")[..12], path); Session = Session with { Objects = [.. Session.Objects, known] };
            }
            id = known.Id;
        }
        var last = Session.Events.LastOrDefault(e => e.Kind is "foreground" or "gap");
        if (last is { Kind: "foreground", DwellSeconds: null } && last.ObjectId == id && sample.Kind == "foreground") return false;
        if (sample.Kind == "foreground" || sample.Kind is "system-locked" or "system-resumed") CloseDwell(sample.AtUtc);
        if (Session.Events.Length >= UsageValidation.MaximumEvents) { State("stopped", "达到 4096 条事件上限，观察已结束。"); return false; }
        Session = Session with { Events = [.. Session.Events, new(sample.AtUtc, sample.Kind, id, sample.Origin)] }; return true;
    }
}
