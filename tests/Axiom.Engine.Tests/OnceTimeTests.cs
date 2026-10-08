using System.Collections.Immutable;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class OnceTimeTests
{
    private static TaskInvocation Invocation() => new(OnceTimeCapabilities.Due, 1,
        new Dictionary<string, string> { ["year"] = "2028", ["month"] = "2", ["day"] = "29", ["hour"] = "9", ["minute"] = "0" }.ToImmutableDictionary());
    [Fact]
    public void RealDatesAndCanonicalResourcesAreValidatedBeforeCompilation()
    {
        var invocation = Invocation(); var spec = OnceTimeCapabilities.Validate(invocation);
        Assert.Equal("2028-02-29T09:00", spec.Resource); Assert.Equal(spec, OnceTimeCapabilities.ParseResource(spec.Resource));
        Assert.Equal("2028-02-29 09:00（一次）", OnceTimeCapabilities.Label(spec.Resource));
        var condition = new OnceTimeTaskDetection().Compile(invocation, ResourceScope.Session("owner", 1));
        Assert.Equal(ConditionKind.Event, condition.Kind); Assert.Equal(spec.Resource, Assert.Single(condition.Dependencies).ResourceId);
        foreach (var (field, value) in new[] { ("year", "2027"), ("year", "9999"), ("month", "13"), ("hour", "1.5"), ("minute", "60") })
            Assert.Throws<ArgumentException>(() => OnceTimeCapabilities.Validate(invocation with { Parameters = invocation.Parameters.SetItem(field, value) }));
        foreach (var invalid in new[] { "2027-02-29T09:00", "2028-2-29T9:00", "2028-02-29T09:00Z", "2028-02-29T09:00:00", "1999-01-01T09:00" })
            Assert.Throws<ArgumentException>(() => OnceTimeCapabilities.ParseResource(invalid));
    }
    [Fact]
    public void DstGapIsSkippedAndRepeatedHourUsesFirstOccurrenceOnly()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("synthetic-once-DST", TimeSpan.FromHours(-5), "synthetic", "standard", "daylight",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2000, 1, 1), new(2099, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))]);
        var missing = OnceTimeCapabilities.ParseResource("2026-03-08T02:30"); Assert.Null(missing.At(zone));
        var gap = new OnceSchedule([missing], new(2026, 3, 8, 0, 0, 0, TimeSpan.Zero), zone);
        Assert.Contains("不存在", Assert.Single(gap.Snapshot).Reason); Assert.Equal(Timeout.InfiniteTimeSpan, gap.Remaining(DateTimeOffset.UnixEpoch));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), OnceTimeCapabilities.ParseResource("2026-11-01T01:30").At(zone));
    }
    [Fact]
    public void ExpiryRollbackAndLongDeadlinesCannotReplayOrOverflowNativeTimer()
    {
        var now = new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.Zero); var spec = OnceTimeCapabilities.ParseResource("2026-10-08T09:00");
        var schedule = new OnceSchedule([spec, spec], now, TimeZoneInfo.Utc); Assert.Single(schedule.Snapshot);
        schedule.Due(now.AddHours(1).AddSeconds(30)); Assert.Equal(1m, Assert.Single(schedule.Snapshot).Count);
        schedule.Reset(now, TimeZoneInfo.Utc); schedule.Due(now.AddHours(1)); Assert.Equal(1m, Assert.Single(schedule.Snapshot).Count);
        Assert.Equal(Timeout.InfiniteTimeSpan, schedule.Remaining(now));
        var missed = new OnceSchedule([spec], now, TimeZoneInfo.Utc); missed.Due(now.AddHours(1).AddMinutes(2));
        Assert.Equal(0m, Assert.Single(missed.Snapshot).Count); Assert.Contains("错过", Assert.Single(missed.Snapshot).Reason);
        var old = new OnceSchedule([spec], now.AddHours(1), TimeZoneInfo.Utc); old.Reset(now, TimeZoneInfo.Utc);
        Assert.Equal(Timeout.InfiniteTimeSpan, old.Remaining(now)); Assert.Contains("过期", Assert.Single(old.Snapshot).Reason);
        var distant = new OnceSchedule([OnceTimeCapabilities.ParseResource("2090-10-08T09:00")], now, TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.FromDays(49), distant.Remaining(now)); distant.Due(now.AddDays(49)); Assert.Equal(0m, Assert.Single(distant.Snapshot).Count);
    }
    [Fact(Timeout = 10000)]
    public async Task OneTimeSourceReschedulesForZoneThenPublishesOnceAndReleasesTimerOnCancel()
    {
        var time = new ManualTimeProvider(); var now = new DateTimeOffset(2026, 10, 8, 8, 59, 0, TimeSpan.Zero); time.JumpUtc(now - time.GetUtcNow());
        var timers = Channel.CreateBounded<TimeSpan>(8); time.TimerCreated = due => Assert.True(timers.Writer.TryWrite(due));
        var changes = Channel.CreateBounded<byte>(1); var updates = Channel.CreateBounded<(bool Snapshot, decimal Count, string Reason)>(4);
        using var lifetime = new CancellationTokenSource(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var zone = TimeZoneInfo.Utc;
        var run = OnceEventSource.RunAsync([OnceTimeCapabilities.ParseResource("2026-10-08T09:00")], changes.Reader, (snapshot, values) =>
        { var value = Assert.Single(values); Assert.True(updates.Writer.TryWrite((snapshot, value.Count, value.Reason))); }, lifetime.Token, time, () => zone);
        Assert.Equal((true, 0m, "等待指定日期和时刻"), await updates.Reader.ReadAsync(timeout.Token));
        Assert.Equal(TimeSpan.FromMinutes(1), await timers.Reader.ReadAsync(timeout.Token));
        zone = TimeZoneInfo.CreateCustomTimeZone("synthetic-west", TimeSpan.FromHours(-1), "synthetic", "synthetic"); changes.Writer.TryWrite(1);
        Assert.Equal(TimeSpan.FromMinutes(61), await timers.Reader.ReadAsync(timeout.Token)); Assert.False(updates.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromMinutes(61)); var fired = await updates.Reader.ReadAsync(timeout.Token);
        Assert.False(fired.Snapshot); Assert.Equal(1m, fired.Count); Assert.Contains("交给规则判断", fired.Reason);
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.All(time.Timers, timer => Assert.Null(timer.At)); Assert.False(updates.Reader.TryRead(out _));
    }
}
