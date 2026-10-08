using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Engine;

public enum DecisionStatus { Released, Ready, Conflict, Overridden, Blocked, Queued, Running, AwaitingObservation, Confirmed, Failed, Unknown }
public sealed record RuleCandidate(string RuleId, string Principal, int Priority, RuleAction Action);
public sealed record ArbitrationResult(DecisionStatus Status, RuleAction? Action, ImmutableArray<RuleCandidate> Winners, string Reason);

public static class RuleArbitrator
{
    public static ArbitrationResult Resolve(IEnumerable<RuleCandidate> eligible, bool overridden = false)
    {
        var candidates = eligible.OrderBy(x => x.RuleId, StringComparer.Ordinal).ToArray();
        if (overridden) return new(DecisionStatus.Overridden, null, [], "ManualOverride");
        if (candidates.Length == 0) return new(DecisionStatus.Released, null, [], "NoEligibleTarget; retain physical state");
        var priority = candidates.Max(x => x.Priority);
        var winners = candidates.Where(x => x.Priority == priority).ToImmutableArray();
        var action = winners[0].Action;
        if (winners.Any(x => x.Action != action)) return new(DecisionStatus.Conflict, null, winners, "EqualPriorityDifferentTargets");
        return new(DecisionStatus.Ready, action, winners, "HighestPriority");
    }
}

public sealed record RuleEvaluationView(string RuleId, long Version, long InputRevision, TruthValue Value, string Reason, long EvaluationCount);
public sealed record ResourceDecision(ResourceKey Key, long Generation, long InputRevision, DecisionStatus Status,
    RuleAction? Action, ImmutableArray<string> Winners, string Reason, Outcome? LastOutcome = null);
