using Axiom.Domain;
using static Axiom.Engine.Tests.AutomationFixture;

namespace Axiom.Engine.Tests;

public sealed class RuleRuntimeTests
{
    [Fact]
    public void OnlyRelevantRulesEvaluateAndUnknownWithdrawsIntent()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule("related"));
        f.Engine.SetRule(f.Rule("other", input: Noise));
        var previous = f.Engine.GetRuleEvaluation("other").EvaluationCount;
        f.Update(true);
        Assert.Equal(previous, f.Engine.GetRuleEvaluation("other").EvaluationCount);
        Assert.Equal(TruthValue.True, f.Engine.GetRuleEvaluation("related").Value);
        Assert.Equal(new[] { "related" }, f.Engine.GetDecision(Output).Winners);
        f.Engine.Update(f.Source, ++f.Sequence, new(Input, StateQuality.Unknown, null, f.Clock.UtcNow));
        Assert.Equal(TruthValue.Unknown, f.Engine.GetRuleEvaluation("related").Value);
        Assert.Equal(DecisionStatus.Released, f.Engine.GetDecision(Output).Status);
    }

    [Fact]
    public void PriorityConflictAndEqualTargetMergeDoNotDependOnArrival()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule("low", priority: 0, target: false));
        f.Engine.SetRule(f.Rule("b", priority: 5));
        f.Engine.SetRule(f.Rule("a", priority: 5));
        f.Update(true);
        Assert.Equal(new[] { "a", "b" }, f.Engine.GetDecision(Output).Winners);
        f.Engine.SetRule(f.Rule("b", priority: 5, target: false, version: 2));
        Assert.Equal(DecisionStatus.Conflict, f.Engine.GetDecision(Output).Status);
        f.Engine.RemoveRule("b");
        Assert.Equal(new[] { "a" }, f.Engine.GetDecision(Output).Winners);
        f.Engine.RemoveRule("a");
        Assert.Equal(new[] { "low" }, f.Engine.GetDecision(Output).Winners);
        Assert.False(f.Engine.GetDecision(Output).Action!.Value.Boolean);
    }

    [Fact]
    public void OverrideBeatsAnyPriorityAndReleaseUsesCurrentCandidates()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule(priority: int.MaxValue));
        f.Update(true);
        Assert.True(f.Engine.TakeOverride(Output, "owner", "manual").UntilRelease);
        Assert.Equal(DecisionStatus.Overridden, f.Engine.GetDecision(Output).Status);
        f.Update(false);
        f.Engine.ReleaseOverride(Output, "owner");
        Assert.Equal(DecisionStatus.Released, f.Engine.GetDecision(Output).Status);
        Assert.False(f.Engine.ReadSnapshot().States[Output].Observation.Value!.Boolean);
        Assert.Throws<UnauthorizedAccessException>(() => f.Engine.TakeOverride(Output, "intruder", "manual"));
    }

    [Fact]
    public void VersionsSchemaAndAuthorizationCannotBeBypassedByDependencyEditing()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule());
        Assert.Throws<ArgumentException>(() => f.Engine.SetRule(f.Rule()));
        Assert.Throws<InvalidOperationException>(() => f.Engine.SetDependencies("rule", []));
        Assert.Throws<InvalidOperationException>(() => f.Engine.RemoveDependencies("rule"));
        f.Update(true);
        f.Authorization.AllowWrite = false;
        f.Engine.RefreshAuthorization();
        Assert.Equal(DecisionStatus.Released, f.Engine.GetDecision(Output).Status);
        Assert.Equal("ActionUnauthorized", f.Engine.GetRuleEvaluation("rule").Reason);
        Assert.Throws<UnauthorizedAccessException>(() => f.Engine.SetRule(f.Rule("denied")));
    }
}
