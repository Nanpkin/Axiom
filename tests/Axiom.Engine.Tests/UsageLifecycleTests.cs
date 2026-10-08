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
    public async Task ImportedPlaceholdersAreExplainedBeforeReviewAndDoNotStartObservationOrSaveRules()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var journal = new UsageJournal(path); var source = new Source(); var recorder = new UsageRecorder(new(5, true, false, []), DateTimeOffset.UtcNow);
            recorder.State("stopped", atUtc: recorder.Session.StartedUtc); journal.Save(recorder.Session);
            var modules = BuiltInAutomationPlugins.Create();
            var catalog = Catalog with { Capabilities = [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
                Combinations = [.. modules.SelectMany(m => m.Combinations)] };
            var proposal = new UsageImport(1, recorder.Session.Id, "existing", "合成提醒", "合成测试候选",
                new(SystemEventCapabilities.Event, 1, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("event", "unlocked")),
                new(OpenTargetCapabilities.Open, 1, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("kind", "url").Add("target", "").Add("delaySeconds", "0")));
            await using var controller = new UsageObservationController(journal, source);
            var response = await controller.HandleAsync(new(1, "usage", "synthetic", Usage: new("import", SessionId: recorder.Session.Id,
                Text: System.Text.Json.JsonSerializer.Serialize(proposal))), catalog, default);
            Assert.True(response.Task!.Paused); Assert.Equal(0, response.Task.Version); Assert.Equal("", response.Task.Action.Parameters["target"]);
            Assert.Contains("仍需手工填写", response.ExportText); Assert.Contains("目录/文档路径或网址", response.ExportText);
            Assert.Contains("才能保存或启用", response.ExportText); Assert.Equal(0, source.Starts); Assert.Null(response.UsageSnapshot!.ActiveId);
            Assert.Single(journal.Read(DateTimeOffset.UtcNow)); Assert.Empty(journal.Dismissed(null));
        }
        finally { File.Delete(path); }
    }

    [Fact(Timeout = 10000)]
    public async Task RestoringHandledPatternsKeepsRecordsAndUnrelatedFeedbackWhileExportMarksThem()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var now = DateTimeOffset.UtcNow;
        UsageSession Session()
        {
            var recorder = new UsageRecorder(new(5, false, true, []), now.AddMinutes(-3));
            for (var i = 0; i < 3; i++)
            {
                recorder.Add(new(recorder.Session.StartedUtc.AddSeconds(i * 30), "process-start", Path.Combine(Path.GetTempPath(), "SyntheticWork.exe")));
                recorder.Add(new(recorder.Session.StartedUtc.AddSeconds(i * 30 + 5), "process-start", Path.Combine(Path.GetTempPath(), "SyntheticCompanion.exe")));
            }
            recorder.State("stopped", atUtc: now); return recorder.Session;
        }
        try
        {
            using var journal = new UsageJournal(path); var source = new Source(); var one = Session(); var two = Session(); journal.Save(one); journal.Save(two);
            await using var controller = new UsageObservationController(journal, source);
            var suggestion = Assert.Single(UsageAnalysis.Analyze(one)); var key = UsageAnalysis.FeedbackKey(one, suggestion);
            journal.Feedback(two.Id, key, "ignored"); journal.Feedback(one.Id, "unrelated-synthetic-feedback", "irrelevant");
            Task<AutomationMessage> Request(string operation, string? id = null) => controller.HandleAsync(new(1, "usage", "synthetic",
                Usage: new(operation, SessionId: id ?? one.Id)), Catalog, default);
            var hidden = (await Request("read")).UsageSnapshot!;
            Assert.Empty(hidden.Suggestions!); Assert.Equal(1, hidden.DismissedSuggestions);
            var exported = (await Request("export")).ExportText!;
            Assert.DoesNotContain(key, exported); Assert.DoesNotContain(one.Objects[0].Executable, exported);
            using (var json = System.Text.Json.JsonDocument.Parse(exported[UsageAnalysis.Prompt.Length..]))
            {
                Assert.Equal(0, json.RootElement.GetProperty("patterns").GetArrayLength());
                Assert.Equal(1, json.RootElement.GetProperty("handledPatterns").GetArrayLength());
            }
            var restored = (await Request("restore-suggestions")).UsageSnapshot!;
            Assert.Single(restored.Suggestions!); Assert.Equal(0, restored.DismissedSuggestions);
            Assert.Equal(one.Events.Length, restored.TotalEvents); Assert.Equal(2, restored.Sessions.Length);
            Assert.Equal(new[] { "unrelated-synthetic-feedback" }, journal.Dismissed(null));
            Assert.Equal(0, source.Starts); Assert.Null(restored.ActiveId);
            await Assert.ThrowsAsync<ArgumentException>(() => Request("restore-suggestions", Guid.NewGuid().ToString("D")));
            Assert.Throws<ArgumentException>(() => UsageValidation.Request(new("restore-suggestions")));
        }
        finally { File.Delete(path); }
    }

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
