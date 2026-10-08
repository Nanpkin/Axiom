using System.Collections.Immutable;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class DailyTimeTests
{
    private static TaskInvocation Invocation() => new(DailyTimeCapabilities.Due, 1,
        DailyTimeCapabilities.Descriptor.Fields.ToImmutableDictionary(f => f.Id, f => f.DefaultValue!));
    [Fact]
    public void WeekdayConfigurationIsMultiSelectCanonicalAndRejectsEmptyOrFractionalInput()
    {
        var invocation = Invocation(); var spec = DailyTimeCapabilities.Validate(invocation);
        Assert.Equal(new DailyTimeSpec(9, 0, 62), spec); Assert.Equal("09:00/3E", spec.Resource);
        Assert.Equal(spec, DailyTimeCapabilities.ParseResource(spec.Resource)); Assert.Equal("周一至周五 09:00", DailyTimeCapabilities.Label(spec.Resource));
        var condition = new DailyTimeTaskDetection().Compile(invocation, ResourceScope.Session("owner", 1));
        Assert.Equal(ConditionKind.Event, condition.Kind); Assert.Equal(spec.Resource, Assert.Single(condition.Dependencies).ResourceId);
        foreach (var (field, value) in new[] { ("hour", "1.5"), ("hour", "24"), ("minute", "60"), ("day0", "yes") })
            Assert.Throws<ArgumentException>(() => DailyTimeCapabilities.Validate(invocation with { Parameters = invocation.Parameters.SetItem(field, value) }));
        var noDays = invocation.Parameters.ToBuilder(); for (var day = 0; day < 7; day++) noDays["day" + day] = "false";
        Assert.Throws<ArgumentException>(() => DailyTimeCapabilities.Validate(invocation with { Parameters = noDays.ToImmutable() }));
        foreach (var invalid in new[] { "9:00/3E", "09:00/3e", "09:00/00", "09:00/FF", "24:00/7F" }) Assert.Throws<ArgumentException>(() => DailyTimeCapabilities.ParseResource(invalid));
    }
    [Fact]
    public void OnlyFutureSelectedDaysAreChosenAndDstMissingOrRepeatedTimesNeverDoubleFire()
    {
        var weekdays = new DailyTimeSpec(9, 0, 62);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero), weekdays.Next(new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        var zone = SyntheticDst();
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero), new DailyTimeSpec(2, 30, 127).Next(new(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), zone));
        var repeated = new DailyTimeSpec(1, 30, 127);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), repeated.Next(new(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), zone));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), repeated.Next(new(2026, 11, 1, 5, 31, 0, TimeSpan.Zero), zone));
    }
    [Fact]
    public void CalendarDeadlinesAllowSmallSchedulingDelayButSkipMissedRunsAndClockRollbackDuplicates()
    {
        var before = new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.Zero); var spec = new DailyTimeSpec(9, 0, 127);
        var schedule = new DailySchedule([spec, spec], before, TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.FromHours(1), schedule.Remaining(before));
        Assert.Equal(new[] { (spec, 1m) }, schedule.Due(before.AddHours(1).AddSeconds(30)));
        Assert.Empty(schedule.Due(before.AddHours(1).AddSeconds(45)));
        schedule.Reset(before, TimeZoneInfo.Utc); Assert.Equal(TimeSpan.FromHours(25), schedule.Remaining(before));
        var missed = new DailySchedule([spec], before, TimeZoneInfo.Utc);
        Assert.Empty(missed.Due(before.AddHours(1).AddMinutes(2)));
        Assert.Equal(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(2), missed.Remaining(before.AddHours(1).AddMinutes(2)));
    }
    [Fact(Timeout = 10000)]
    public async Task ClockNotificationsRescheduleOneDeadlineWithoutReplayingMissedTimesAndCancelReleasesIt()
    {
        var time = new ManualTimeProvider(); time.JumpUtc(new DateTimeOffset(2026, 10, 8, 8, 59, 0, TimeSpan.Zero) - time.GetUtcNow());
        var deadlines = Channel.CreateBounded<TimeSpan>(8); time.TimerCreated = due => Assert.True(deadlines.Writer.TryWrite(due));
        var changes = Channel.CreateBounded<byte>(1); var events = Channel.CreateBounded<decimal>(4);
        using var lifetime = new CancellationTokenSource(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = DailyEventSource.RunAsync([new(9, 0, 127)], changes.Reader, (_, count) => Assert.True(events.Writer.TryWrite(count)), lifetime.Token, time, () => TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.FromMinutes(1), await deadlines.Reader.ReadAsync(timeout.Token));
        time.JumpUtc(TimeSpan.FromHours(2)); Assert.True(changes.Writer.TryWrite(1));
        Assert.Equal(TimeSpan.FromHours(22) + TimeSpan.FromMinutes(1), await deadlines.Reader.ReadAsync(timeout.Token)); Assert.False(events.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromHours(22) + TimeSpan.FromMinutes(1)); Assert.Equal(1m, await events.Reader.ReadAsync(timeout.Token));
        Assert.Equal(TimeSpan.FromHours(24), await deadlines.Reader.ReadAsync(timeout.Token));
        time.JumpUtc(TimeSpan.FromHours(-1)); Assert.True(changes.Writer.TryWrite(1));
        Assert.Equal(TimeSpan.FromHours(25), await deadlines.Reader.ReadAsync(timeout.Token)); Assert.False(events.Reader.TryRead(out _));
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.All(time.Timers, timer => Assert.Null(timer.At));
    }
    [Fact(Timeout = 10000)]
    public async Task LostClockSubscriptionEndsInsteadOfSpinningOrKeepingADeadlineAlive()
    {
        var time = new ManualTimeProvider(); var changes = Channel.CreateBounded<byte>(1);
        var run = DailyEventSource.RunAsync([new(9, 0, 127)], changes.Reader, (_, _) => throw new InvalidOperationException("Unexpected event"), default, time, () => TimeZoneInfo.Utc);
        changes.Writer.Complete(); await Assert.ThrowsAsync<ChannelClosedException>(() => run);
        Assert.All(time.Timers, timer => Assert.Null(timer.At));
    }
    private static TimeZoneInfo SyntheticDst() => TimeZoneInfo.CreateCustomTimeZone("synthetic-DST", TimeSpan.FromHours(-5), "synthetic", "standard", "daylight",
        [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2000, 1, 1), new(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))]);
}
