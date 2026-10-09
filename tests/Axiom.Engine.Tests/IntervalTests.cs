using System.Collections.Immutable;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class IntervalTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("1441")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData(" 5")]
    [InlineData("1e2")]
    public void InvalidIntervalsCannotCompile(string value) => Assert.Throws<ArgumentException>(() => new IntervalTaskDetection().Compile(
        new(IntervalCapabilities.Elapsed, 1, ImmutableDictionary<string, string>.Empty.Add("minutes", value)), ResourceScope.Session("owner", 1)));

    [Fact]
    public void CanonicalIntervalsShareOneEventResourceAndOnlyRegisteredActionsAreOffered()
    {
        var compiler = new IntervalTaskDetection();
        var invocation = new TaskInvocation(IntervalCapabilities.Elapsed, 1, ImmutableDictionary<string, string>.Empty.Add("minutes", "0030"));
        var condition = compiler.Compile(invocation, ResourceScope.Session("owner", 1));
        Assert.Equal(ConditionKind.Event, condition.Kind); Assert.Equal("30", Assert.Single(condition.Dependencies).ResourceId);
        var canonical = compiler.Compile(invocation with { Parameters = invocation.Parameters.SetItem("minutes", "30") }, ResourceScope.Session("owner", 1));
        Assert.Equal(condition.Kind, canonical.Kind); Assert.Equal(condition.Key, canonical.Key); Assert.Equal(condition.Expected, canonical.Expected);
        Assert.True(condition.Dependencies.SetEquals(canonical.Dependencies));
        Assert.Contains(new IntervalPlugin().Combinations, c => c.ActionId == OpenTargetCapabilities.Open);
        Assert.DoesNotContain(new IntervalPlugin().Combinations, c => c.ActionId == HotkeyCapabilities.Send);
        Assert.Throws<ArgumentException>(() => IntervalCapabilities.Validate(invocation with { Parameters = invocation.Parameters.Add("script", "ignored") }));
    }
    [Fact]
    public void SharedDeadlineDeduplicatesIntervalsAndCoalescesMissedPeriodsWithoutBacklog()
    {
        var schedule = new IntervalSchedule([1, 5, 1]);
        Assert.Equal(TimeSpan.FromMinutes(1), schedule.Remaining(TimeSpan.Zero)); Assert.Empty(schedule.Due(TimeSpan.Zero));
        Assert.Equal(new[] { (1, 1m) }, schedule.Due(TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromMinutes(1), schedule.Remaining(TimeSpan.FromMinutes(1)));
        Assert.Equal(new[] { (1, 2m), (5, 1m) }, schedule.Due(TimeSpan.FromHours(3)));
        Assert.Empty(schedule.Due(TimeSpan.FromHours(3)));
        Assert.Equal(TimeSpan.FromMinutes(1), schedule.Remaining(TimeSpan.FromHours(3)));
        Assert.Equal(new[] { (1, 3m) }, schedule.Due(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => new IntervalSchedule([])); Assert.Throws<ArgumentException>(() => new IntervalSchedule([0]));
        Assert.Empty(new IntervalSchedule([1]).Due(TimeSpan.Zero)); // reconnect starts a fresh full period
    }
    [Fact(Timeout = 10000)]
    public async Task MonotonicSourceUsesOneDeadlineAndCancellationReleasesIt()
    {
        var time = new ManualTimeProvider(); using var lifetime = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = Channel.CreateBounded<(int, decimal)>(4);
        var run = IntervalEventSource.RunAsync([1, 5, 1], (minutes, count) => Assert.True(events.Writer.TryWrite((minutes, count))), lifetime.Token, time);
        Assert.Single(time.Timers, t => t.At.HasValue); Assert.False(events.Reader.TryRead(out _));
        time.JumpUtc(TimeSpan.FromDays(100)); Assert.False(events.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromHours(5));
        Assert.Equal((1, 1m), await events.Reader.ReadAsync(timeout.Token));
        Assert.Equal((5, 1m), await events.Reader.ReadAsync(timeout.Token));
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.All(time.Timers, t => Assert.Null(t.At)); Assert.False(events.Reader.TryRead(out _));
    }
}
