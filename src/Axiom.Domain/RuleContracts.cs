using System.Collections.Immutable;

namespace Axiom.Domain;

public enum TruthValue { Unknown, False, True }
public enum ScalarKind { Boolean, Number, Text }
public enum ConditionKind { Equal, Hysteresis, All, Any, Not, Event }
public enum RuleActionKind { Desired, Command }
public enum CapabilityOperation { Observe, ApplyDesired, ExecuteCommand, Override, ReleaseOverride }

/// <summary>A closed expression tree, never user code. Factories bound its size/depth.</summary>
public sealed record RuleCondition
{
    public ConditionKind Kind { get; }
    public ResourceKey? Key { get; }
    public StateValue? Expected { get; }
    public decimal Enter { get; }
    public decimal Exit { get; }
    public ImmutableArray<RuleCondition> Children { get; }
    public ImmutableHashSet<ResourceKey> Dependencies { get; }
    public int Nodes { get; }
    public int Depth { get; }

    private RuleCondition(ConditionKind kind, ResourceKey? key, StateValue? expected,
        decimal enter, decimal exit, ImmutableArray<RuleCondition> children)
    {
        (Kind, Key, Expected, Enter, Exit, Children) = (kind, key, expected, enter, exit, children);
        Nodes = 1 + children.Sum(x => x.Nodes);
        Depth = 1 + (children.IsEmpty ? 0 : children.Max(x => x.Depth));
        if (Nodes > 256 || Depth > 16) throw new ArgumentException("Expression exceeds 256 nodes or depth 16.");
        Dependencies = key is null ? children.SelectMany(x => x.Dependencies).ToImmutableHashSet() : [key];
    }
    public static RuleCondition Equal(ResourceKey key, StateValue expected) => new(ConditionKind.Equal,
        key ?? throw new ArgumentNullException(nameof(key)), expected ?? throw new ArgumentNullException(nameof(expected)), 0, 0, []);
    // A monotonically increasing event counter. Snapshot values are baselines, never replayed events.
    public static RuleCondition Event(ResourceKey key, ValueSchema schema) => new(ConditionKind.Event,
        key ?? throw new ArgumentNullException(nameof(key)), StateValue.From(schema, 0m), 0, 0, []);
    public static RuleCondition Hysteresis(ResourceKey key, ValueSchema schema, decimal enter, decimal exit)
    {
        if (enter <= exit) throw new ArgumentException("Enter threshold must exceed exit threshold.");
        return new(ConditionKind.Hysteresis, key ?? throw new ArgumentNullException(nameof(key)), StateValue.From(schema, 0m), enter, exit, []);
    }
    public static RuleCondition All(params RuleCondition[] conditions) => Composite(ConditionKind.All, conditions);
    public static RuleCondition Any(params RuleCondition[] conditions) => Composite(ConditionKind.Any, conditions);
    public static RuleCondition Not(RuleCondition condition) => Composite(ConditionKind.Not, [condition]);
    private static RuleCondition Composite(ConditionKind kind, RuleCondition[] conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.Length is < 1 or > 256 || conditions.Any(x => x is null)) throw new ArgumentException("Invalid expression children.");
        if (conditions.Any(x => x.Kind == ConditionKind.Event)) throw new ArgumentException("Events must be standalone command conditions.");
        return new(kind, null, null, 0, 0, [.. conditions]);
    }
}

public sealed record RuleAction
{
    public RuleActionKind Kind { get; }
    public ResourceKey Key { get; }
    public StateValue Value { get; }
    public bool IsIdempotent { get; }
    private RuleAction(RuleActionKind kind, ResourceKey key, StateValue value, bool idempotent)
    {
        Kind = kind;
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        IsIdempotent = idempotent;
    }
    public static RuleAction Desired(ResourceKey key, StateValue target) => new(RuleActionKind.Desired, key, target, true);
    public static RuleAction Command(ResourceKey key, StateValue arguments, bool idempotent = false) => new(RuleActionKind.Command, key, arguments, idempotent);
}

public sealed record RuleTiming
{
    public TimeSpan Debounce { get; }
    public TimeSpan MinimumDuration { get; }
    public TimeSpan Cooldown { get; }
    public TimeSpan? MaximumInputAge { get; }
    public RuleTiming(TimeSpan debounce = default, TimeSpan minimumDuration = default, TimeSpan cooldown = default, TimeSpan? maximumInputAge = null)
    {
        if (debounce < TimeSpan.Zero || minimumDuration < TimeSpan.Zero || cooldown < TimeSpan.Zero || maximumInputAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce));
        (Debounce, MinimumDuration, Cooldown, MaximumInputAge) = (debounce, minimumDuration, cooldown, maximumInputAge);
    }
}

public sealed record RuleDefinition
{
    public string Id { get; }
    public long Version { get; }
    public string Principal { get; }
    public ResourceScope Scope { get; }
    public RuleCondition Condition { get; }
    public RuleAction Action { get; }
    public int Priority { get; }
    public bool Enabled { get; }
    public RuleTiming Timing { get; }
    public RuleRetention Retention { get; }
    public RuleDefinition(string id, long version, string principal, ResourceScope scope, RuleCondition condition,
        RuleAction action, int priority = 0, bool enabled = true, RuleTiming? timing = null, RuleRetention? retention = null)
    {
        Id = Contract.Text(id, nameof(id));
        Version = Contract.Positive(version, nameof(version));
        Principal = Contract.Text(principal, nameof(principal));
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Action = action ?? throw new ArgumentNullException(nameof(action));
        if (condition.Kind == ConditionKind.Event && action.Kind != RuleActionKind.Command) throw new ArgumentException("Events require command actions.");
        if (action.Key.Scope != scope || condition.Dependencies.Any(k => k.Scope != scope && k.Scope.Kind != ScopeKind.Machine))
            throw new ArgumentException("Rule action and non-machine inputs must belong to the rule scope.");
        (Priority, Enabled) = (priority, enabled);
        Timing = timing ?? new();
        Retention = retention ?? new();
    }
}

public sealed record CapabilityBinding
{
    public CapabilityDescriptor Descriptor { get; }
    public ScalarKind ParametersKind { get; }
    public ScalarKind ResultKind { get; }
    public CapabilityBinding(CapabilityDescriptor descriptor, ScalarKind parametersKind, ScalarKind resultKind)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        ParametersKind = Contract.EnumValue(parametersKind);
        ResultKind = Contract.EnumValue(resultKind);
    }
    public bool Accepts(StateValue value) => value.Schema == Descriptor.Parameters && value.Kind == ParametersKind;
}
