using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.ServiceHost;
using Microsoft.Data.Sqlite;

namespace Axiom.Engine.Tests;

public sealed class UsageLifecycleTests
{
    private static readonly AutomationSnapshot Catalog = new([], [], [], true, "ready", "", DateTimeOffset.UtcNow, []);
    private sealed class Source : IUsageObservationSource
    {
        public int Active, Starts;
        public bool Fail;
        public Action<UsageSample>? Publish;
        public readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RunAsync(UsageOptions options, Action ready, Action<UsageSample> publish, CancellationToken token)
        {
            if (Fail) throw new IOException("synthetic source unavailable");
            Active++; Starts++; Publish = publish; ready();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Active--; Publish = null; Released.TrySetResult(); }
        }
    }
    private static Task<AutomationMessage> Send(UsageObservationController controller, string operation, UsageOptions? options = null) =>
        controller.HandleAsync(new(1, "usage", "test", Usage: new(operation, options)), Catalog, default);

    [Fact(Timeout = 10000)]
    public async Task DuplicateAndOutOfScopeSamplesDoNotRewriteStorageButGapsAndExpiryStillPersist()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var journal = new UsageJournal(path); var source = new Source();
            await using var controller = new UsageObservationController(journal, source);
            var started = (await Send(controller, "start", new(5, true, false, []))).UsageSnapshot!.Selected!;
            using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); probe.Open();
            using (var setup = probe.CreateCommand())
            {
                setup.CommandText = "CREATE TABLE write_probe(n INTEGER NOT NULL); INSERT INTO write_probe VALUES(0); CREATE TRIGGER count_observation_update AFTER UPDATE ON usage_sessions BEGIN UPDATE write_probe SET n=n+1; END;";
                setup.ExecuteNonQuery();
            }
            long Writes() { using var read = probe.CreateCommand(); read.CommandText = "SELECT n FROM write_probe"; return (long)read.ExecuteScalar()!; }
            var now = DateTimeOffset.UtcNow; var app = Path.Combine(Path.GetTempPath(), "Example.exe");
            source.Publish!(new(now, "foreground", app)); var recorded = Writes(); Assert.Equal(1, recorded);
            source.Publish!(new(now.AddSeconds(1), "foreground", app));
            source.Publish!(new(now.AddSeconds(2), "process-start", app)); Assert.Equal(recorded, Writes());
            source.Publish!(new(now.AddSeconds(3), "foreground-unobserved")); Assert.Equal(recorded + 1, Writes());
            source.Publish!(new(now.AddSeconds(4), "foreground-unobserved")); Assert.Equal(recorded + 1, Writes());
            source.Publish!(new(started.EndsUtc, "foreground", app)); Assert.Equal(recorded + 2, Writes());
            var stopped = Assert.Single(journal.Read(DateTimeOffset.UtcNow)); Assert.Equal("stopped", stopped.State);
            Assert.Equal(2, stopped.Events.Length); Assert.Contains(stopped.Events, e => e.Kind == "gap");
        }
        finally { File.Delete(path); }
    }

    [Fact(Timeout = 10000)]
    public async Task PurposeCanBeAmendedAfterObservationWithoutChangingEvidenceOrRestartingCapture()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var journal = new UsageJournal(path); var source = new Source();
            var controller = new UsageObservationController(journal, source);
            var id = (await Send(controller, "start", new(5, true, false, []))).UsageSnapshot!.ActiveId!;
            source.Publish!(new(DateTimeOffset.UtcNow, "foreground", Path.Combine(Path.GetTempPath(), "Example.exe")));
            await Send(controller, "stop"); var original = Assert.Single(journal.Read(DateTimeOffset.UtcNow));
            var changed = await controller.HandleAsync(new(1, "usage", "test", Usage: new("annotate", SessionId: id, Text: "整理写作环境")), Catalog, default);
            Assert.Equal("整理写作环境", changed.UsageSnapshot!.Selected!.Options.Purpose);
            Assert.Equal(original.Events, changed.UsageSnapshot.Selected.Events); Assert.Equal(original.StoppedUtc, changed.UsageSnapshot.Selected.StoppedUtc);
            Assert.Equal(1, source.Starts); Assert.Equal(0, source.Active);
            await controller.DisposeAsync(); Assert.Equal("整理写作环境", Assert.Single(journal.Read(DateTimeOffset.UtcNow)).Options.Purpose);
            Assert.Throws<ArgumentException>(() => UsageValidation.Request(new("annotate", SessionId: id, Text: new string('x', 501))));
            Assert.Throws<ArgumentException>(() => UsageValidation.Request(new("annotate", Text: "missing session")));
        }
        finally { File.Delete(path); }
    }

    [Fact(Timeout = 10000)]
    public async Task PauseResumeAndUiLeaseReleaseOnlyObservationAndPreserveItsOriginalDeadline()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var journal = new UsageJournal(path); var source = new Source();
            await using var controller = new UsageObservationController(journal, source);
            var started = (await Send(controller, "start", new(5, true, true, []))).UsageSnapshot!;
            Assert.Equal(1, source.Active); var id = started.ActiveId!; var endSignal = controller.WaitForEndAsync(id, default); Assert.False(endSignal.IsCompleted); var deadline = started.Selected!.EndsUtc;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Send(controller, "start", new(5, true, false, [])));
            source.Publish!(new(DateTimeOffset.UtcNow, "foreground", Path.Combine(Path.GetTempPath(), "Example.exe")));
            var paused = (await Send(controller, "pause")).UsageSnapshot!; Assert.Equal(0, source.Active); Assert.Equal("paused", paused.Selected!.State);
            var resumed = (await Send(controller, "resume")).UsageSnapshot!; Assert.Equal(1, source.Active); Assert.Equal(deadline, resumed.Selected!.EndsUtc);
            await controller.EndLeaseAsync(id); await endSignal; Assert.Equal(0, source.Active);
            Assert.Null((await Send(controller, "read")).UsageSnapshot!.ActiveId);
            Assert.Equal("stopped", Assert.Single(journal.Read(DateTimeOffset.UtcNow)).State);
        }
        finally { File.Delete(path); }
    }
    [Fact(Timeout = 10000)]
    public async Task AbsoluteExpiryReleasesSourceAndAnOldUiLeaseCannotStopANewSession()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var journal = new UsageJournal(path); var source = new Source(); var time = new ManualTimeProvider();
            await using var controller = new UsageObservationController(journal, source, time);
            var first = (await Send(controller, "start", new(1, true, false, []))).UsageSnapshot!.ActiveId!;
            time.Advance(TimeSpan.FromMinutes(1)); await source.Released.Task;
            var expired = (await Send(controller, "read")).UsageSnapshot!; Assert.Null(expired.ActiveId); Assert.Equal(0, source.Active);
            var second = (await Send(controller, "start", new(1, true, false, []))).UsageSnapshot!.ActiveId!;
            await controller.EndLeaseAsync(first); Assert.Equal(1, source.Active);
            await controller.EndLeaseAsync(second); Assert.Equal(0, source.Active);
        }
        finally { File.Delete(path); }
    }
    [Fact(Timeout = 10000)]
    public async Task FailedSourceCannotBeReportedAsRecordingAndClearStopsAnActiveSource()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var journal = new UsageJournal(path); var source = new Source { Fail = true };
            await using var controller = new UsageObservationController(journal, source);
            await Assert.ThrowsAsync<IOException>(() => Send(controller, "start", new(5, true, false, [])));
            var failed = (await Send(controller, "read")).UsageSnapshot!; Assert.Null(failed.ActiveId); Assert.NotEmpty(failed.Selected!.Gaps);
            source.Fail = false; await Send(controller, "start", new(5, true, false, [])); await Send(controller, "clear");
            Assert.Equal(0, source.Active); Assert.Empty(journal.Read(DateTimeOffset.UtcNow));
        }
        finally { File.Delete(path); }
    }
}
