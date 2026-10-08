using Axiom.Domain;
using static Axiom.Engine.Tests.AutomationFixture;

namespace Axiom.Engine.Tests;

public sealed class RuleTimeTests
{
    [Fact]
    public void DebounceAndMinimumDurationNeverSwallowFalseOrUnknown()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule(timing: new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3))));
        f.Update(true);
        Assert.NotEqual(TruthValue.True, f.Engine.GetRuleEvaluation("rule").Value);
        Assert.Equal(1, f.Scheduler.Pending);
        f.Scheduler.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("MinimumDuration", f.Engine.GetRuleEvaluation("rule").Reason);
        f.Scheduler.Advance(TimeSpan.FromSeconds(2));
        f.Update(false);
        Assert.Equal(0, f.Engine.PendingDeadlines);
        Assert.Equal(DecisionStatus.Released, f.Engine.GetDecision(Output).Status);
        f.Update(true);
        f.Scheduler.Advance(TimeSpan.FromSeconds(2));
        f.Scheduler.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TruthValue.True, f.Engine.GetRuleEvaluation("rule").Value);
        f.Engine.Update(f.Source, ++f.Sequence, new(Input, StateQuality.Unknown, null, f.Clock.UtcNow));
        Assert.Equal(TruthValue.Unknown, f.Engine.GetRuleEvaluation("rule").Value);
        Assert.Equal(DecisionStatus.Released, f.Engine.GetDecision(Output).Status);
        Assert.Equal(0, f.Scheduler.Pending);
    }

    [Fact]
    public void OneNearestTimerCoversManyRulesAndRemovalCancelsIt()
    {
        using var f = new AutomationFixture();
        for (var i = 0; i < 30; i++) f.Engine.SetRule(f.Rule($"r{i}", timing: new(minimumDuration: TimeSpan.FromSeconds(i + 1))));
        f.Update(true);
        Assert.Equal(30, f.Engine.PendingDeadlines);
        Assert.Equal(1, f.Scheduler.Pending);
        for (var i = 0; i < 30; i++) f.Engine.RemoveRule($"r{i}");
        Assert.Equal(0, f.Engine.PendingDeadlines);
        Assert.Equal(0, f.Scheduler.Pending);
        var scheduled = f.Scheduler.Scheduled;
        f.Scheduler.Advance(TimeSpan.FromDays(1));
        Assert.Equal(scheduled, f.Scheduler.Scheduled);
    }

    [Fact]
    public void FreshnessIsAnExplicitDeadlineAndSameValueRefreshRestoresKnown()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule(timing: new(maximumInputAge: TimeSpan.FromSeconds(5))));
        f.Update(true);
        f.Scheduler.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TruthValue.Unknown, f.Engine.GetRuleEvaluation("rule").Value);
        Assert.Equal("InputStale", f.Engine.GetRuleEvaluation("rule").Reason);
        f.Update(true);
        Assert.Equal(TruthValue.True, f.Engine.GetRuleEvaluation("rule").Value);
        f.Engine.Stop();
        Assert.Equal(0, f.Scheduler.Pending);
    }

    [Fact]
    public void OverrideExpiryUsesLatestStateAndUntilReleaseHasNoTimer()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule());
        f.Engine.TakeOverride(Output, "owner", "until released");
        Assert.Equal(0, f.Scheduler.Pending);
        f.Engine.TakeOverride(Output, "owner", "temporary", TimeSpan.FromSeconds(10));
        f.Update(true); f.Update(false);
        f.Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(DecisionStatus.Released, f.Engine.GetDecision(Output).Status);
        Assert.Equal(0, f.Scheduler.Pending);
    }
}
