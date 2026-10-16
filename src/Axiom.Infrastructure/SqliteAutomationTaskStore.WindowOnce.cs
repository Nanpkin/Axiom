using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;

namespace Axiom.Infrastructure;

public sealed record WindowOnceAttemptState(string Status, int Attempts, DateTimeOffset? RetryAt);

public sealed partial class SqliteAutomationTaskStore
{
    private void CreateWindowOnceAttempts()
    {
        using var cmd = connection.CreateCommand();
        // Additive metadata: old task JSON/schema remains readable and is never rewritten here.
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS window_once_attempts(task_id TEXT NOT NULL, window_date INTEGER NOT NULL, attempt_id TEXT NOT NULL, status TEXT NOT NULL, attempts INTEGER NOT NULL, retry_at INTEGER, PRIMARY KEY(task_id,window_date));";
        cmd.ExecuteNonQuery();
    }
    public async Task<WindowOnceAttemptState?> ReadWindowOnceAttemptAsync(string taskId, DateOnly date, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT status,attempts,retry_at FROM window_once_attempts WHERE task_id=$task AND window_date=$date;";
            cmd.Parameters.AddWithValue("$task", taskId); cmd.Parameters.AddWithValue("$date", date.DayNumber);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero)) : null;
        }
        finally { gate.Release(); }
    }
    public async Task<bool> BeginWindowOnceAttemptAsync(string taskId, long version, DateOnly date, Guid attemptId, DateTimeOffset now, CancellationToken token)
    {
        if (!Guid.TryParseExact(taskId, "D", out _) || version < 1 || attemptId == Guid.Empty) throw new ArgumentException("时段执行标识无效。");
        await gate.WaitAsync(token);
        try
        {
            using var tx = connection.BeginTransaction(); using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT configuration FROM automation_tasks WHERE id=$task AND version=$version;";
            cmd.Parameters.AddWithValue("$task", taskId); cmd.Parameters.AddWithValue("$version", version);
            var json = cmd.ExecuteScalar() as string;
            var task = json is null ? null : JsonSerializer.Deserialize<AutomationTask>(json);
            if (task is null || task.Paused || task.Detection.CapabilityId != WindowOnceCapabilities.Due) return false;
            var spec = WindowOnceCapabilities.Validate(task.Detection);
            cmd.CommandText = "INSERT INTO window_once_attempts(task_id,window_date,attempt_id,status,attempts,retry_at) VALUES($task,$date,$attempt,'started',1,NULL) ON CONFLICT(task_id,window_date) DO UPDATE SET attempt_id=$attempt,status='started',attempts=attempts+1,retry_at=NULL WHERE status='failed' AND retry_at<=$now AND attempts<=$maximum;";
            cmd.Parameters.AddWithValue("$date", date.DayNumber); cmd.Parameters.AddWithValue("$attempt", attemptId.ToString("N"));
            cmd.Parameters.AddWithValue("$now", now.UtcTicks); cmd.Parameters.AddWithValue("$maximum", spec.MaxRetries);
            var accepted = cmd.ExecuteNonQuery() == 1;
            token.ThrowIfCancellationRequested(); tx.Commit(); return accepted;
        }
        finally { gate.Release(); }
    }
    public async Task CompleteWindowOnceAttemptAsync(string taskId, DateOnly date, Guid attemptId, OutcomeStatus outcome,
        DateTimeOffset now, int retryMinutes, CancellationToken token = default)
    {
        if (attemptId == Guid.Empty || retryMinutes is < 1 or > 1440 || outcome is not (OutcomeStatus.Succeeded or OutcomeStatus.Failed or OutcomeStatus.Rejected or OutcomeStatus.Unknown))
            throw new ArgumentException("时段执行结果无效。");
        await gate.WaitAsync(token);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE window_once_attempts SET status=$status,retry_at=$retry WHERE task_id=$task AND window_date=$date AND attempt_id=$attempt AND status='started';";
            var retry = outcome is OutcomeStatus.Failed or OutcomeStatus.Rejected;
            cmd.Parameters.AddWithValue("$status", retry ? "failed" : outcome == OutcomeStatus.Succeeded ? "succeeded" : "unknown");
            cmd.Parameters.AddWithValue("$retry", retry ? now.AddMinutes(retryMinutes).UtcTicks : DBNull.Value);
            cmd.Parameters.AddWithValue("$task", taskId); cmd.Parameters.AddWithValue("$date", date.DayNumber); cmd.Parameters.AddWithValue("$attempt", attemptId.ToString("N"));
            token.ThrowIfCancellationRequested();
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("时段执行记录已变化，结果未持久确认；不会自动重试。");
        }
        finally { gate.Release(); }
    }
    public async Task AbandonUnsentWindowOnceAttemptAsync(string taskId, DateOnly date, Guid attemptId, DateTimeOffset now, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var tx = connection.BeginTransaction(); using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$task", taskId); cmd.Parameters.AddWithValue("$date", date.DayNumber); cmd.Parameters.AddWithValue("$attempt", attemptId.ToString("N"));
            cmd.CommandText = "DELETE FROM window_once_attempts WHERE task_id=$task AND window_date=$date AND attempt_id=$attempt AND status='started' AND attempts=1;";
            if (cmd.ExecuteNonQuery() == 0)
            {
                cmd.CommandText = "UPDATE window_once_attempts SET status='failed',attempts=attempts-1,retry_at=$now WHERE task_id=$task AND window_date=$date AND attempt_id=$attempt AND status='started' AND attempts>1;";
                cmd.Parameters.AddWithValue("$now", now.UtcTicks); cmd.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested(); tx.Commit();
        }
        finally { gate.Release(); }
    }
}
