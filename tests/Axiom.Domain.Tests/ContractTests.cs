using Axiom.Domain;

namespace Axiom.Domain.Tests;

public sealed class ContractTests
{
    private static readonly ResourceKey Key = new("test.setting", "stable-instance", ResourceScope.Machine);
    private static readonly ValueSchema Schema = new("boolean", 1);
    private static readonly StateValue Value = StateValue.From(Schema, true);
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void ScopeSeparatesUsersAndSessions()
    {
        Assert.NotEqual(ResourceScope.User("u1"), ResourceScope.User("u2"));
        Assert.NotEqual(ResourceScope.Session("u1", 1), ResourceScope.Session("u1", 2));
        Assert.NotEqual(ResourceScope.User("u1"), ResourceScope.Session("u1", 1));
        Assert.Equal(new ResourceKey("c", "r", ResourceScope.Session("u", 0)), new("c", "r", ResourceScope.Session("u", 0)));
        Assert.Throws<ArgumentException>(() => ResourceScope.User(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResourceScope.Session("u", -1));
        Assert.Throws<ArgumentException>(() => new ResourceKey("", "r", ResourceScope.Machine));
        Assert.Throws<ArgumentNullException>(() => new ResourceKey("c", "r", null!));
    }

    [Fact]
    public void TypedValuesAreVersionedAndUnknownDoesNotInventFalse()
    {
        Assert.Equal(StateValue.From(Schema, false), StateValue.From(new("boolean", 1), false));
        Assert.NotEqual(StateValue.From(Schema, false), StateValue.From(new("boolean", 2), false));
        Assert.NotEqual(StateValue.From(Schema, false), StateValue.From(Schema, "false"));
        Assert.Null(new Observation(Key, StateQuality.Unknown, null, Now).Value);
        Assert.Throws<ArgumentException>(() => new Observation(Key, StateQuality.Known, null, Now));
        Assert.Throws<ArgumentException>(() => new Observation(Key, StateQuality.Unknown, Value, Now));
        Assert.Throws<ArgumentException>(() => new Observation(Key, StateQuality.Unavailable, Value, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Observation(Key, (StateQuality)99, null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ValueSchema("s", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSession("source", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObservedState(new(Key, StateQuality.Known, Value, Now), new("s", 1), -1, 1));
    }

    [Fact]
    public void CommandAndDesiredRemainDifferentContracts()
    {
        var command = new Command(Guid.NewGuid(), Key, Value, "user", Now.AddSeconds(1), false);
        var desired = new DesiredState(Key, Value, "rule", 1, 1, "while enabled");
        Assert.False(typeof(Command).IsAssignableFrom(typeof(DesiredState)));
        Assert.False(typeof(DesiredState).IsAssignableFrom(typeof(Command)));
        Assert.False(command.IsIdempotent);
        Assert.Equal(1, desired.Generation);
        Assert.Throws<ArgumentException>(() => new Command(Guid.Empty, Key, Value, "u", Now, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesiredState(Key, Value, "r", 0, 0, "enabled"));
        Assert.Throws<ArgumentException>(() => new DesiredState(Key, Value, "r", 0, 1, ""));
        Assert.Throws<ArgumentException>(() => new Outcome(OutcomeStatus.Unknown, "timeout", Guid.Empty));
    }

    [Fact]
    public void OverrideAndModeDoNotSilentlyRelaxGuarantees()
    {
        Assert.True(new ManualOverride(Key, "user", 1, Now, "manual").UntilRelease);
        Assert.True(new RuleRetention().KeepInLightweight);
        Assert.Throws<ArgumentException>(() => new ManualOverride(Key, "u", 1, Now, "manual", Now));
        Assert.Throws<ArgumentException>(() => new ModeState(EngineMode.Lightweight, EngineMode.Normal, 1, TransitionStatus.Effective, "done"));
        var exceptions = new[] { "required observer" };
        var mode = new ModeState(EngineMode.Lightweight, EngineMode.Normal, 1, TransitionStatus.Blocked, "required", exceptions);
        exceptions[0] = "mutated";
        Assert.Equal("required observer", Assert.Single(mode.Exceptions));
    }

    [Fact]
    public void CapabilityRequiresAbsoluteSettingsAndExplicitValidPolicies()
    {
        CapabilityDescriptor Create(bool absolute = true, bool observe = true, TimeSpan? timeout = null) => new(
            "setting", Schema, Schema, ScopeKind.User, observe, false, true, ExecutionLocation.SessionAgent,
            "user.setting", true, "available", absolute, timeout ?? TimeSpan.FromSeconds(1), true,
            ConcurrencyScope.Resource, RetryCategory.IdempotentTransientOnly, ConfirmationSource.Observation,
            EventFidelity.EdgeSensitive, TimeSpan.FromSeconds(1), TimeSpan.Zero, CostClass.Low, true,
            "active dependency", "same-user", [Key]);
        Assert.True(Create().SharedByReferenceCount);
        Assert.False(Create().RequiresPolling);
        Assert.Throws<ArgumentException>(() => Create(absolute: false));
        Assert.Throws<ArgumentException>(() => Create(observe: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(timeout: TimeSpan.Zero));
    }
}
