using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Engine;

public sealed record ConditionEvaluation(TruthValue Value, ImmutableDictionary<RuleCondition, TruthValue> Hysteresis);

public static class RuleEvaluator
{
    public static ConditionEvaluation Evaluate(RuleCondition condition, IReadOnlyDictionary<ResourceKey, ObservedState> states,
        ImmutableDictionary<RuleCondition, TruthValue>? history = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(states);
        var next = (history ?? ImmutableDictionary<RuleCondition, TruthValue>.Empty).ToBuilder();
        TruthValue Visit(RuleCondition node)
        {
            if (node.Key is { } key)
            {
                if (!states.TryGetValue(key, out var state) || state.Observation.Quality != StateQuality.Known ||
                    state.Observation.Value is not { } value || value.Schema != node.Expected!.Schema || value.Kind != node.Expected.Kind)
                {
                    if (node.Kind == ConditionKind.Hysteresis) next[node] = TruthValue.Unknown;
                    return TruthValue.Unknown;
                }
                if (node.Kind == ConditionKind.Equal) return value == node.Expected ? TruthValue.True : TruthValue.False;
                if (node.Kind == ConditionKind.Event) return value.Number > 0 ? TruthValue.True : TruthValue.False;
                var result = value.Number >= node.Enter ? TruthValue.True : value.Number <= node.Exit ? TruthValue.False :
                    next.GetValueOrDefault(node, TruthValue.Unknown);
                next[node] = result;
                return result;
            }
            var children = node.Children.Select(Visit).ToArray();
            return node.Kind switch
            {
                ConditionKind.Not => children[0] switch { TruthValue.True => TruthValue.False, TruthValue.False => TruthValue.True, _ => TruthValue.Unknown },
                ConditionKind.All => children.Contains(TruthValue.False) ? TruthValue.False : children.Contains(TruthValue.Unknown) ? TruthValue.Unknown : TruthValue.True,
                ConditionKind.Any => children.Contains(TruthValue.True) ? TruthValue.True : children.Contains(TruthValue.Unknown) ? TruthValue.Unknown : TruthValue.False,
                _ => throw new InvalidOperationException("Invalid closed expression.")
            };
        }
        var result = Visit(condition);
        return new(result, next.ToImmutable());
    }
}
