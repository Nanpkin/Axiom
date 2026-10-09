using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class WindowedIntervalTests
{
    private static TaskInvocation Invocation() => new(WindowedIntervalCapabilities.Due, 1,
        WindowedIntervalCapabilities.Descriptor.Fields.ToImmutableDictionary(f => f.Id, f => f.DefaultValue!));
    [Fact]
    public void WindowConfigurationUsesRealRegistryAndCanonicalResources()
    {
        var invocation = Invocation(); var spec = WindowedIntervalCapabilities.Validate(invocation);
        Assert.Equal(new WindowedIntervalSpec(30, 540, 1080, 62), spec);
        Assert.Equal("30@09:00-18:00/3E", spec.Resource);
        Assert.Equal(spec, WindowedIntervalCapabilities.ParseResource(spec.Resource));
        Assert.Contains("周一至周五", WindowedIntervalCapabilities.Label(spec.Resource));
        var condition = new WindowedIntervalTaskDetection().Compile(invocation, ResourceScope.Session("owner", 1));
        Assert.Equal(ConditionKind.Event, condition.Kind); Assert.Equal(spec.Resource, Assert.Single(condition.Dependencies).ResourceId);
        Assert.Contains(BuiltInAutomationPlugins.Create().SelectMany(p => p.Combinations), c => c == new TaskCombination(WindowedIntervalCapabilities.Due, NotificationCapabilities.Show));
        foreach (var (field, value) in new[] { ("minutes", "0.5"), ("minutes", "0"), ("minutes", "540"), ("startHour", "24"), ("endMinute", "60"), ("day0", "yes"), ("endHour", "9") })
            Assert.Throws<ArgumentException>(() => WindowedIntervalCapabilities.Validate(invocation with { Parameters = invocation.Parameters.SetItem(field, value) }));
        var noDays = invocation.Parameters.ToBuilder(); for (var d = 0; d < 7; d++) noDays["day" + d] = "false";
        Assert.Throws<ArgumentException>(() => WindowedIntervalCapabilities.Validate(invocation with { Parameters = noDays.ToImmutable() }));
        foreach (var invalid in new[] { "030@09:00-18:00/3E", "30@9:00-18:00/3E", "30@09:00-18:00/3e", "30@09:00-18:00/00", "30@09:00-09:00/7F", "30@23:00-24:00/7F" })
            Assert.Throws<ArgumentException>(() => WindowedIntervalCapabilities.ParseResource(invalid));
    }
    [Fact]
    public void WorkdaySlotsAreAnchoredAfterStartAndExcludeEndAndWeekends()
    {
        var spec = new WindowedIntervalSpec(30, 540, 1080, 62); var friday = new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(friday.AddHours(1.5), spec.Next(friday, TimeZoneInfo.Utc));
        Assert.Equal(friday.AddHours(2.5), spec.Next(friday.AddHours(2).AddMinutes(10), TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero), spec.Next(friday.AddHours(9.5), TimeZoneInfo.Utc));
        Assert.True(spec.Contains(friday.AddHours(1), TimeZoneInfo.Utc)); Assert.False(spec.Contains(friday.AddHours(10), TimeZoneInfo.Utc));
    }
    [Fact]
    public void OvernightWindowsBelongToTheirStartWeekday()
    {
        var fridayOnly = new WindowedIntervalSpec(30, 22 * 60, 60, 1 << (int)DayOfWeek.Friday);
        var friday = new DateTimeOffset(2026, 10, 9, 23, 40, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), fridayOnly.Next(friday, TimeZoneInfo.Utc));
        Assert.True(fridayOnly.Contains(friday.AddMinutes(50), TimeZoneInfo.Utc)); Assert.False(fridayOnly.Contains(friday.AddMinutes(80), TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 22, 30, 0, TimeSpan.Zero), fridayOnly.Next(new(2026, 10, 10, 0, 30, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.Contains("次日", WindowedIntervalCapabilities.Label(fridayOnly.Resource));
    }
    [Fact]
    public void MissingDstSlotsAreSkippedAndRepeatedClockSlotsUseOnlyFirstOccurrence()
    {
        var zone = SyntheticDst(); var spec = new WindowedIntervalSpec(30, 60, 240, 127);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), spec.Next(new(2026, 3, 8, 6, 31, 0, TimeSpan.Zero), zone));
        var repeated = new WindowedIntervalSpec(30, 0, 180, 127);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), repeated.Next(new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), zone));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero), repeated.Next(new(2026, 11, 1, 5, 31, 0, TimeSpan.Zero), zone));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero), repeated.Next(new(2026, 11, 1, 6, 10, 0, TimeSpan.Zero), zone));
    }
    [Fact]
    public void SmallDelayMustStayInsideWindowAndMissedOrAlreadyFiredSlotsNeverCatchUp()
    {
        var before = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero); var spec = new WindowedIntervalSpec(1, 540, 543, 127);
        var schedule = new WindowedIntervalSchedule([spec, spec], before, TimeZoneInfo.Utc);
        Assert.Equal(new[] { (spec, 1m) }, schedule.Due(before.AddMinutes(1).AddSeconds(30)));
        Assert.Empty(schedule.Due(before.AddMinutes(3))); // even a one-minute late callback must not cross the end
        schedule.Reset(before, TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.FromMinutes(2), schedule.Remaining(before)); // 09:01 already fired, only 09:02 remains
        Assert.Equal(new[] { (spec, 2m) }, schedule.Due(before.AddMinutes(2)));
        schedule.Reset(before, TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1), schedule.Remaining(before));
        var missed = new WindowedIntervalSchedule([spec], before, TimeZoneInfo.Utc); Assert.Empty(missed.Due(before.AddHours(2)));
        Assert.Equal(TimeSpan.FromHours(22) + TimeSpan.FromMinutes(1), missed.Remaining(before.AddHours(2)));
        schedule.Reset(before.AddDays(-90), TimeZoneInfo.Utc); Assert.Equal(TimeSpan.FromDays(49), schedule.Remaining(before.AddDays(-90)));
    }
    [Fact(Timeout = 10000)]
    public async Task ClockChangesRecomputeOneDeadlineAndCancellationReleasesSubscriptionWait()
    {
        var time = new ManualTimeProvider(); time.JumpUtc(new DateTimeOffset(2026, 10, 8, 9, 29, 0, TimeSpan.Zero) - time.GetUtcNow());
        var deadlines = Channel.CreateBounded<TimeSpan>(8); time.TimerCreated = d => Assert.True(deadlines.Writer.TryWrite(d));
        var changes = Channel.CreateBounded<byte>(1); var events = Channel.CreateBounded<decimal>(4);
        using var lifetime = new CancellationTokenSource(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = WindowedIntervalEventSource.RunAsync([new(30, 540, 1080, 62)], changes.Reader, (_, n) => Assert.True(events.Writer.TryWrite(n)), lifetime.Token, time, () => TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.FromMinutes(1), await deadlines.Reader.ReadAsync(timeout.Token));
        time.JumpUtc(TimeSpan.FromHours(10)); Assert.True(changes.Writer.TryWrite(1));
        Assert.Equal(TimeSpan.FromHours(14) + TimeSpan.FromMinutes(1), await deadlines.Reader.ReadAsync(timeout.Token)); Assert.False(events.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromHours(14) + TimeSpan.FromMinutes(1)); Assert.Equal(1m, await events.Reader.ReadAsync(timeout.Token));
        Assert.Equal(TimeSpan.FromMinutes(30), await deadlines.Reader.ReadAsync(timeout.Token));
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.All(time.Timers, t => Assert.Null(t.At));
    }
    [Fact(Timeout = 10000)]
    public async Task ClosedClockMailboxFaultsAndReleasesTimerRatherThanSpinning()
    {
        var time = new ManualTimeProvider(); var changes = Channel.CreateBounded<byte>(1);
        var run = WindowedIntervalEventSource.RunAsync([new(30, 540, 1080, 127)], changes.Reader, (_, _) => throw new InvalidOperationException(), default, time, () => TimeZoneInfo.Utc);
        changes.Writer.Complete(); await Assert.ThrowsAsync<ChannelClosedException>(() => run); Assert.All(time.Timers, t => Assert.Null(t.At));
    }
    [Fact]
    public void ImportedWindowSettingsAreStrictEvenWhenReminderTextAwaitsUserInput()
    {
        var recorder = new UsageRecorder(new(5, true, false, []), DateTimeOffset.UtcNow); var modules = BuiltInAutomationPlugins.Create();
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(p => p.Detections.Select(d => d.Descriptor).Concat(p.Actions.Select(a => a.Descriptor)))], [.. modules.SelectMany(p => p.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var reminder = new TaskInvocation(NotificationCapabilities.Show, 1, NotificationCapabilities.Descriptor.Fields.Where(f => f.DefaultValue is not null).ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).SetItem("title", "").Add("body", ""));
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "工作时段提醒", "合成候选", Invocation(), reminder);
        Assert.True(UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!.Paused);
        foreach (var (field, value) in new[] { ("minutes", "0.5"), ("minutes", "540"), ("endHour", "9") })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = proposal.Detection! with { Parameters = proposal.Detection.Parameters.SetItem(field, value) } }), recorder.Session, catalog));
    }
    private static TimeZoneInfo SyntheticDst() => TimeZoneInfo.CreateCustomTimeZone("synthetic-window-DST", TimeSpan.FromHours(-5), "synthetic", "standard", "daylight",
        [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2000, 1, 1), new(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))]);
}
