using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class ActivityHistoryTests
{
    [Fact]
    public async Task CompleteDetailsAreDurableAuthorizedAndFetchedIndependentlyOfListSummaries()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-detail-" + Guid.NewGuid().ToString("N") + ".db");
        var at = DateTimeOffset.UtcNow;
        var prefix = "完成 1 个，无需处理 1 个。";
        var text = prefix + "\n\n【完成】已隐藏\nC:\\Synthetic\\One.exe\n" + new string('详', 5000) + "\n【无需处理】\nC:\\Synthetic\\Last.exe";
        long id;
        try
        {
            using (var store = new SqliteActivityStore(path))
            {
                await using var journal = new ActivityJournal(store);
                using var fixture = new Fixture(); fixture.Engine.ConfigureActivityHistory(journal, "owner");
                journal.Record(new(at, "托盘", "检查完成", text));
                journal.Record(new(at, "托盘", "检查完成", text + "不同详情"));
                var page = await fixture.Engine.ReadActivityAsync("owner", null, 64, default);
                id = page.Records[1].Id;
                Assert.All(page.Records, row => Assert.Equal(prefix, row.Event.Detail));
                Assert.NotEqual(page.Records[0].DetailFingerprint, page.Records[1].DetailFingerprint);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Engine.ReadActivityDetailAsync("other", id, default));
                Assert.Equal(text, (await fixture.Engine.ReadActivityDetailAsync("owner", id, default))!.Event.Detail);
            }
            using (var reopened = new SqliteActivityStore(path))
            {
                Assert.Equal(text, reopened.ReadDetail(id)!.Event.Detail);
                reopened.ClearBefore(at.AddSeconds(1)); Assert.Null(reopened.ReadDetail(id));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DetailRequestsRejectUnrelatedFieldsAndMaximumEscapedBodiesFitOneFrame()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new AutomationMessage(1, "read-activity-detail", "detail", now.AddSeconds(10), Id: "12");
        AutomationProtocol.ValidateRequest(request, now);
        foreach (var id in new[] { "", "0", "-1", "01", "+1", "x", "18446744073709551615" })
            Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Id = id }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request, now, true));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { ActivityPageSize = 1 }, now));
        var record = new ActivityRecord(12, new(now, "测试", "完整详情", new string('\u0001', ActivityJournal.MaximumDetailLength)));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { ActivityDetail = record }, now));
        using var stream = new MemoryStream();
        await AutomationProtocol.WriteAsync(stream, new(1, "activity-detail", "detail", ActivityDetail: record), default);
        Assert.True(stream.Length <= AutomationProtocol.MaximumFrame + 4);
        stream.Position = 0;
        Assert.Equal(record, (await AutomationProtocol.ReadAsync(stream, default)).ActivityDetail);
    }

    [Fact]
    public async Task MaximumSummaryPageStaysBoundedAndUnavailableDetailsAreExplicit()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-summary-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new SqliteActivityStore(path); await using var journal = new ActivityJournal(store);
            var entry = new ActivityEvent(DateTimeOffset.UtcNow, new string('\u0001', 40), new string('\u0001', 120),
                new string('\u0001', ActivityJournal.MaximumDetailLength), Subject: new string('\u0001', 120));
            for (var i = 0; i < 64; i++) journal.Record(entry);
            var page = await journal.ReadAsync(null, 64, default);
            Assert.Equal(64, page.Records.Length); Assert.All(page.Records, r => Assert.True(r.Event.Detail.Length <= 256));
            using var stream = new MemoryStream();
            await AutomationProtocol.WriteAsync(stream, new(1, "activity", "summary", Activity: page), default);
            Assert.True(stream.Length <= AutomationProtocol.MaximumFrame + 4);
        }
        finally { File.Delete(path); }
        await using var missing = new ActivityJournal(null, "不可用");
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.ReadDetailAsync(1, default));
    }

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
