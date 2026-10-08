using Axiom.Domain;

namespace Axiom.Engine.Tests;

internal sealed class FakeAuthorization : IAuthorizationPolicy
{
    internal bool AllowWrite = true;
    internal bool AllowObserve = true;
    public AuthorizationDecision Authorize(string principal, ResourceKey resource, CapabilityOperation operation) =>
        new(principal == "owner" && (operation == CapabilityOperation.Observe ? AllowObserve : AllowWrite), "fake policy");
}
internal sealed class SequentialIds : ICommandIdSource
{
    private int sequence;
    public Guid NextId() => new(++sequence, 0, 0, new byte[8]);
}
internal sealed class AutomationFixture : IDisposable
{
    internal static readonly ValueSchema Schema = new("bool", 1);
    internal static readonly ResourceKey Input = new("input", "input", ResourceScope.Machine);
    internal static readonly ResourceKey Output = new("output", "output", ResourceScope.Machine);
    internal static readonly ResourceKey Noise = new("input", "noise", ResourceScope.Machine);
    internal FakeClock Clock { get; } = new();
    internal FakeScheduler Scheduler { get; }
    internal FakeAuthorization Authorization { get; } = new();
    internal EngineHost Engine { get; }
    internal SourceSession Source { get; }
    internal long Sequence;
    internal AutomationFixture(ICapabilityExecutor? executor = null)
    {
        Scheduler = new(Clock);
        Engine = new(Clock, Scheduler, executor ?? new FakeExecutor());
        Engine.Start();
        Engine.ConfigureAutomation(Authorization, new SequentialIds());
        Engine.RegisterCapability(Binding("input"));
        Engine.RegisterCapability(Binding("output"));
        Source = Engine.RegisterSource("source", [Input, Output, Noise]);
        Engine.ApplySnapshot(Source, 0, [Observe(Input, false), Observe(Output, false), Observe(Noise, false)]);
    }
    internal static CapabilityBinding Binding(string id, bool available = true, bool cancellable = true, bool lightweight = true,
        IEnumerable<ResourceKey>? dependencies = null, TimeSpan? interval = null, TimeSpan? timeout = null,
        ValueSchema? schema = null, EventFidelity fidelity = EventFidelity.EdgeSensitive) => new(
        new(id, schema ?? Schema, schema ?? Schema, ScopeKind.Machine, true, true, true, ExecutionLocation.Service, "fake", available, "fake",
            true, timeout ?? TimeSpan.FromSeconds(10), cancellable, ConcurrencyScope.Resource, RetryCategory.IdempotentTransientOnly,
            ConfirmationSource.Observation, fidelity, TimeSpan.FromSeconds(1), interval ?? TimeSpan.Zero,
            CostClass.Low, lightweight, "active dependency", "fake owner", dependencies ?? []), ScalarKind.Boolean, ScalarKind.Boolean);
    internal static Observation Observe(ResourceKey key, bool value) => new(key, StateQuality.Known, StateValue.From(Schema, value), DateTimeOffset.UnixEpoch);
    internal RuleDefinition Rule(string id = "rule", int priority = 0, bool target = true, RuleTiming? timing = null, bool command = false,
        bool keep = true, long version = 1, ResourceKey? input = null, ResourceKey? output = null) => new(id, version, "owner", ResourceScope.Machine,
            RuleCondition.Equal(input ?? Input, StateValue.From(Schema, true)),
            command ? RuleAction.Command(output ?? Output, StateValue.From(Schema, target)) : RuleAction.Desired(output ?? Output, StateValue.From(Schema, target)),
            priority, timing: timing, retention: new(keep));
    internal void Update(bool value, ResourceKey? key = null) => Engine.Update(Source, ++Sequence, Observe(key ?? Input, value));
    public void Dispose() => Engine.Dispose();
}
