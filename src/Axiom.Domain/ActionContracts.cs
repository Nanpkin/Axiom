using System.Collections.Immutable;

namespace Axiom.Domain;

public sealed record DesiredState
{
    public ResourceKey Key { get; }
    public StateValue Target { get; }
    public string Owner { get; }
    public int Priority { get; }
    public long Generation { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public string RevocationCondition { get; }
    public DesiredState(ResourceKey key, StateValue target, string owner, int priority, long generation,
        string revocationCondition, DateTimeOffset? expiresAt = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Owner = Contract.Text(owner, nameof(owner));
        Priority = priority;
        Generation = Contract.Positive(generation, nameof(generation));
        RevocationCondition = Contract.Text(revocationCondition, nameof(revocationCondition));
        ExpiresAt = expiresAt;
    }
}

public sealed record CommandOrigin(string RuleId, long RuleVersion, StateValue? EventStamp = null, ResourceKey? EventSource = null);

public sealed record Command
{
    public Guid CommandId { get; }
    public ResourceKey Key { get; }
    public StateValue Arguments { get; }
    public string Principal { get; }
    public DateTimeOffset Deadline { get; }
    public bool IsIdempotent { get; }
    public ImmutableArray<CommandOrigin> Origins { get; }
    public Command(Guid commandId, ResourceKey key, StateValue arguments, string principal, DateTimeOffset deadline, bool isIdempotent,
        ImmutableArray<CommandOrigin> origins = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("Command id is required.", nameof(commandId));
        CommandId = commandId;
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        Principal = Contract.Text(principal, nameof(principal));
        Deadline = deadline;
        IsIdempotent = isIdempotent;
        Origins = origins.IsDefault ? [] : origins;
    }
}

public enum OutcomeStatus { Accepted, Rejected, Pending, Succeeded, Failed, Unknown }
public enum FailureKind { None, Transient, Permanent }
public sealed record Outcome
{
    public OutcomeStatus Status { get; }
    public string Reason { get; }
    public Guid CorrelationId { get; }
    public FailureKind Failure { get; }
    public Outcome(OutcomeStatus status, string reason, Guid correlationId, FailureKind failure = FailureKind.None)
    {
        Status = Contract.EnumValue(status);
        Failure = Contract.EnumValue(failure);
        Reason = Contract.Text(reason, nameof(reason));
        if (correlationId == Guid.Empty) throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        CorrelationId = correlationId;
    }
}

public sealed record ManualOverride
{
    public ResourceKey Key { get; }
    public string Owner { get; }
    public long CreatedRevision { get; }
    public DateTimeOffset StartsAt { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public string Reason { get; }
    public bool UntilRelease => ExpiresAt is null;
    public ManualOverride(ResourceKey key, string owner, long createdRevision, DateTimeOffset startsAt, string reason, DateTimeOffset? expiresAt = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Owner = Contract.Text(owner, nameof(owner));
        CreatedRevision = Contract.Positive(createdRevision, nameof(createdRevision));
        if (expiresAt <= startsAt) throw new ArgumentException("Expiry must follow start.", nameof(expiresAt));
        (StartsAt, ExpiresAt) = (startsAt, expiresAt);
        Reason = Contract.Text(reason, nameof(reason));
    }
}

public enum EngineMode { Normal, Lightweight }
public enum TransitionStatus { Pending, Effective, Blocked, Failed }
public sealed record RuleRetention(bool KeepInLightweight = true);
public sealed record ModeState
{
    public EngineMode Requested { get; }
    public EngineMode Effective { get; }
    public long Generation { get; }
    public TransitionStatus Status { get; }
    public string Reason { get; }
    public System.Collections.Immutable.ImmutableArray<string> Exceptions { get; }
    public ModeState(EngineMode requested, EngineMode effective, long generation, TransitionStatus status, string reason, IEnumerable<string>? exceptions = null)
    {
        Requested = Contract.EnumValue(requested);
        Effective = Contract.EnumValue(effective);
        Generation = Contract.Positive(generation, nameof(generation));
        Status = Contract.EnumValue(status);
        if (status == TransitionStatus.Effective && requested != effective) throw new ArgumentException("Effective mode must match the request.");
        Reason = Contract.Text(reason, nameof(reason));
        Exceptions = System.Collections.Immutable.ImmutableArray.CreateRange((exceptions ?? []).Select(x => Contract.Text(x, nameof(exceptions))));
    }
}
