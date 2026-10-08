namespace Axiom.Domain;

public static class Contract
{
    public static string Text(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
    public static long Positive(long value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name);
    public static T EnumValue<T>(T value) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
}

public enum ScopeKind { Machine, User, Session }

public sealed record ResourceScope
{
    public ScopeKind Kind { get; }
    public string? UserId { get; }
    public int? SessionId { get; }
    private ResourceScope(ScopeKind kind, string? userId, int? sessionId) => (Kind, UserId, SessionId) = (kind, userId, sessionId);
    public static ResourceScope Machine { get; } = new(ScopeKind.Machine, null, null);
    public static ResourceScope User(string userId) => new(ScopeKind.User, Contract.Text(userId, nameof(userId)), null);
    public static ResourceScope Session(string userId, int sessionId) => sessionId >= 0
        ? new(ScopeKind.Session, Contract.Text(userId, nameof(userId)), sessionId)
        : throw new ArgumentOutOfRangeException(nameof(sessionId));
}

public sealed record ResourceKey
{
    public string CapabilityId { get; }
    public string ResourceId { get; }
    public ResourceScope Scope { get; }
    public ResourceKey(string capabilityId, string resourceId, ResourceScope scope)
    {
        CapabilityId = Contract.Text(capabilityId, nameof(capabilityId));
        ResourceId = Contract.Text(resourceId, nameof(resourceId));
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }
}

public sealed record ValueSchema
{
    public string Id { get; }
    public int Version { get; }
    public ValueSchema(string id, int version)
    {
        Id = Contract.Text(id, nameof(id));
        Version = checked((int)Contract.Positive(version, nameof(version)));
    }
}

// Closed immutable scalar union. No arbitrary mutable object or executable payload.
public sealed record StateValue
{
    public ScalarKind Kind => Boolean.HasValue ? ScalarKind.Boolean : Number.HasValue ? ScalarKind.Number : ScalarKind.Text;
    public ValueSchema Schema { get; }
    public bool? Boolean { get; }
    public decimal? Number { get; }
    public string? Text { get; }
    private StateValue(ValueSchema schema, bool? boolean, decimal? number, string? text)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        (Boolean, Number, Text) = (boolean, number, text);
    }
    public static StateValue From(ValueSchema schema, bool value) => new(schema, value, null, null);
    public static StateValue From(ValueSchema schema, decimal value) => new(schema, null, value, null);
    public static StateValue From(ValueSchema schema, string value) => new(schema, null, null, value ?? throw new ArgumentNullException(nameof(value)));
}

public enum StateQuality { Known, Unknown, Stale, Unavailable }

public sealed record Observation
{
    public ResourceKey Key { get; }
    public StateQuality Quality { get; }
    public StateValue? Value { get; }
    public DateTimeOffset ObservedAt { get; }
    public Observation(ResourceKey key, StateQuality quality, StateValue? value, DateTimeOffset observedAt)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Quality = Contract.EnumValue(quality);
        if (quality == StateQuality.Known && value is null ||
            quality is StateQuality.Unknown or StateQuality.Unavailable && value is not null)
            throw new ArgumentException("Known needs a value; Unknown/Unavailable cannot claim one.", nameof(value));
        Value = value;
        ObservedAt = observedAt;
    }
}

public sealed record SourceSession
{
    public string SourceId { get; }
    public long Epoch { get; }
    public SourceSession(string sourceId, long epoch)
    {
        SourceId = Contract.Text(sourceId, nameof(sourceId));
        Epoch = Contract.Positive(epoch, nameof(epoch));
    }
}

public sealed record ObservedState
{
    public Observation Observation { get; }
    public SourceSession Source { get; }
    public long Sequence { get; }
    public long Revision { get; }
    public ObservedState(Observation observation, SourceSession source, long sequence, long revision)
    {
        Observation = observation ?? throw new ArgumentNullException(nameof(observation));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if (sequence < 0 || revision < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        (Sequence, Revision) = (sequence, revision);
    }
}
