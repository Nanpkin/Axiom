using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class ActivityHistoryTests
{
    [Fact]
    public async Task DurablePagesIncludeAllEventsAndNewAppendsDoNotShiftOlderPages()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-activity-" + Guid.NewGuid().ToString("N") + ".db");
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        try
        {
            using (var store = new SqliteActivityStore(path))
            {
                await using var journal = new ActivityJournal(store);
                for (var i = 0; i < 83; i++) journal.Record(new(at.AddSeconds(i), "检测", "程序已启动", "日志 " + i, Subject: "测试规则"));
                var first = await journal.ReadAsync(null, CancellationToken.None);
                Assert.Equal(SqliteActivityStore.PageSize, first.Records.Length);
                Assert.Null(first.Error);
                var ids = first.Records.Select(x => x.Id).ToList();
                journal.Record(new(at.AddSeconds(100), "后台", "新增活动", "读取历史期间追加"));
                var cursor = first.BeforeId;
                while (cursor is not null)
                {
                    var page = await journal.ReadAsync(cursor, CancellationToken.None);
                    ids.AddRange(page.Records.Select(x => x.Id)); cursor = page.BeforeId;
                }
                Assert.Equal(83, ids.Count);
                Assert.Equal(83, ids.Distinct().Count());
                Assert.Equal(Enumerable.Range(1, 83).Select(x => (long)x).Reverse(), ids);
            }
            using (var reopened = new SqliteActivityStore(path))
            {
                var first = reopened.Read(null);
                Assert.Equal("新增活动", first.Records[0].Event.Title);
                Assert.Equal("测试规则", first.Records[1].Event.Subject);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CleanupUsesExactUtcSevenDayBoundaryAndSurvivesRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-activity-clear-" + Guid.NewGuid().ToString("N") + ".db");
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var cutoff = now.AddDays(-7);
        try
        {
            using (var store = new SqliteActivityStore(path))
            {
                await using var journal = new ActivityJournal(store);
                using var fixture = new Fixture();
                fixture.Engine.ConfigureActivityHistory(journal, "owner");
                journal.Record(new(cutoff.AddTicks(-1), "测试", "旧", "应删除"));
                journal.Record(new(cutoff.ToOffset(TimeSpan.FromHours(-4)), "测试", "边界", "应保留"));
                journal.Record(new(now, "测试", "新", "应保留"));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Engine.ReadActivityAsync("other", null, CancellationToken.None));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Engine.ClearOldActivityAsync("other", now, CancellationToken.None));
                var cleared = await fixture.Engine.ClearOldActivityAsync("owner", now, CancellationToken.None);
                Assert.Equal(1, cleared.DeletedCount);
                Assert.Equal(cutoff, cleared.BeforeUtc);
                Assert.Equal(0, (await journal.ClearBeforeAsync(cutoff, CancellationToken.None)).DeletedCount);
            }
            using var reopened = new SqliteActivityStore(path);
            Assert.Equal(new[] { "新", "边界" }, reopened.Read(null).Records.Select(x => x.Event.Title));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UnavailableLoggingReportsFailureAndNeverClaimsSuccessfulCleanup()
    {
        await using var journal = new ActivityJournal(null, "磁盘不可用");
        journal.Record(new(DateTimeOffset.UnixEpoch, "后台", "启动", "正在运行"));
        Assert.Contains("磁盘不可用", (await journal.ReadAsync(null, CancellationToken.None)).Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ClearBeforeAsync(DateTimeOffset.UnixEpoch, CancellationToken.None));
    }

    [Fact]
    public async Task HistoryWireFramesStayBoundedAndRequestCannotSupplyCleanupDateOrResponseFields()
    {
        var now = DateTimeOffset.UtcNow;
        var valid = new AutomationMessage(1, "read-activity", "history", now.AddSeconds(10), ActivityBeforeId: 12);
        AutomationProtocol.ValidateRequest(valid, now);
        AutomationProtocol.ValidateRequest(valid with { Type = "clear-old-activity", ActivityBeforeId = null }, now);
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(valid with { ActivityBeforeId = 0 }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(valid with { Type = "clear-old-activity" }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(valid with { Activity = new([], null) }, now));
        var entry = new ActivityEvent(now, new string('测', 40), new string('测', 120), new string('测', 1000), Subject: new string('测', 120));
        using var frame = new MemoryStream();
        await AutomationProtocol.WriteAsync(frame, new(1, "activity", "history", Activity: new(
            Enumerable.Range(1, SqliteActivityStore.PageSize).Select(x => new ActivityRecord(x, entry)).ToArray(), 1)), CancellationToken.None);
        Assert.True(frame.Length <= AutomationProtocol.MaximumFrame + 4);
        frame.Position = 0;
        Assert.Equal(SqliteActivityStore.PageSize, (await AutomationProtocol.ReadAsync(frame, CancellationToken.None)).Activity!.Records.Length);
    }
}
