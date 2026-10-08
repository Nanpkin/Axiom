using Axiom.Domain;

namespace Axiom.Engine.Tests;

public sealed class RuleExpressionTests
{
    private static readonly ResourceKey Key = new("input", "r", ResourceScope.Machine);
    private static readonly ValueSchema Schema = new("bool", 1);
    private static Dictionary<ResourceKey, ObservedState> States(StateValue? value, StateQuality quality = StateQuality.Known) =>
        new() { [Key] = new(new(Key, quality, value, DateTimeOffset.UnixEpoch), new("s", 1), 1, 1) };

    [Fact]
    public void UnknownNeverBecomesFalseThroughNegationOrSchemaMismatch()
    {
        var condition = RuleCondition.Not(RuleCondition.Equal(Key, StateValue.From(Schema, true)));
        Assert.Equal(TruthValue.Unknown, RuleEvaluator.Evaluate(condition, States(null, StateQuality.Unknown)).Value);
        Assert.Equal(TruthValue.Unknown, RuleEvaluator.Evaluate(condition, States(StateValue.From(new("bool", 2), false))).Value);
        Assert.Equal(TruthValue.Unknown, RuleEvaluator.Evaluate(condition, States(StateValue.From(Schema, "false"))).Value);
        Assert.Equal(TruthValue.True, RuleEvaluator.Evaluate(condition, States(StateValue.From(Schema, false))).Value);
    }

    [Fact]
    public void HysteresisKeepsHistoryBetweenThresholdsAndResetsOnUnknown()
    {
        var schema = new ValueSchema("number", 1);
        var condition = RuleCondition.Hysteresis(Key, schema, 80, 60);
        var initial = RuleEvaluator.Evaluate(condition, States(StateValue.From(schema, 70m)));
        Assert.Equal(TruthValue.Unknown, initial.Value);
        var enter = RuleEvaluator.Evaluate(condition, States(StateValue.From(schema, 80m)), initial.Hysteresis);
        Assert.Equal(TruthValue.True, enter.Value);
        Assert.Equal(TruthValue.True, RuleEvaluator.Evaluate(condition, States(StateValue.From(schema, 70m)), enter.Hysteresis).Value);
        Assert.Equal(TruthValue.False, RuleEvaluator.Evaluate(condition, States(StateValue.From(schema, 60m)), enter.Hysteresis).Value);
        var gap = RuleEvaluator.Evaluate(condition, States(null, StateQuality.Unknown), enter.Hysteresis);
        Assert.Equal(TruthValue.Unknown, RuleEvaluator.Evaluate(condition, States(StateValue.From(schema, 70m)), gap.Hysteresis).Value);
    }

    [Fact]
    public void ExpressionFactoriesBoundDepthAndExtractAllDependencies()
    {
        var leaf = RuleCondition.Equal(Key, StateValue.From(Schema, true));
        Assert.Equal(Key, Assert.Single(RuleCondition.All(leaf, RuleCondition.Not(leaf)).Dependencies));
        Assert.Throws<ArgumentException>(() => RuleCondition.Any());
        Assert.Throws<ArgumentException>(() => RuleCondition.Hysteresis(Key, Schema, 1, 1));
        var deep = leaf;
        for (var i = 1; i < 16; i++) deep = RuleCondition.Not(deep);
        Assert.Throws<ArgumentException>(() => RuleCondition.Not(deep));
    }

    [Fact]
    public void RuleScopeVersionAndTimingAreValidated()
    {
        var leaf = RuleCondition.Equal(Key, StateValue.From(Schema, true));
        var action = RuleAction.Desired(Key, StateValue.From(Schema, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuleDefinition("r", 0, "u", ResourceScope.Machine, leaf, action));
        Assert.Throws<ArgumentException>(() => new RuleDefinition("r", 1, "u", ResourceScope.User("u"), leaf, action));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuleTiming(cooldown: TimeSpan.FromTicks(-1)));
        Assert.True(new RuleDefinition("r", 1, "u", ResourceScope.Machine, leaf, action).Retention.KeepInLightweight);
    }
}
