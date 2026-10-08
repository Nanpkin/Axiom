using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Engine.Benchmarks;

// Controlled ports for Engine-only measurements; no actual provider, executor or timer.
internal sealed class ControlledPorts : IEngineClock, IDeadlineScheduler, ICapabilityExecutor
{
    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + MonotonicNow;
    public TimeSpan MonotonicNow { get; private set; }
    public int ScheduleCalls { get; private set; }
    public int ExecutionCalls { get; private set; }
    public void Advance(TimeSpan elapsed) => MonotonicNow += elapsed;
    public IDisposable Schedule(TimeSpan deadline, Action callback)
    {
        ScheduleCalls++;
        throw new InvalidOperationException("Bootstrap Engine must not create deadlines.");
    }
    public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken cancellationToken)
    {
        ExecutionCalls++;
        throw new InvalidOperationException("No actions in bootstrap benchmark.");
    }
    public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken cancellationToken)
    {
        ExecutionCalls++;
        throw new InvalidOperationException("No actions in bootstrap benchmark.");
    }
}

internal sealed class CoreFixture : IDisposable
{
    internal static readonly ResourceKey Key = new("test", "affected", ResourceScope.Machine);
    private static readonly ResourceKey Other = new("test", "unrelated", ResourceScope.Machine);
    private static readonly ValueSchema Schema = new("bool", 1);
    private static readonly Observation True = new(Key, StateQuality.Known, StateValue.From(Schema, true), DateTimeOffset.UnixEpoch);
    private static readonly Observation False = new(Key, StateQuality.Known, StateValue.From(Schema, false), DateTimeOffset.UnixEpoch);
    internal ControlledPorts Ports { get; } = new();
    internal EngineHost Engine { get; }
    internal SourceSession Source { get; }
    internal StateSubscription? Subscription { get; }
    private long sequence;

    internal CoreFixture(int rules, bool subscribe = false, int capacity = 256)
    {
        Engine = new(Ports, Ports, Ports);
        Engine.Start();
        Source = Engine.RegisterSource("fake", [Key]);
        Engine.ApplySnapshot(Source, 0, [False]);
        for (var i = 0; i < rules; i++) Engine.SetDependencies($"r{i:D5}", [i < 10 ? Key : Other]);
        if (subscribe) Subscription = Engine.Subscribe([Key], capacity).Subscription;
    }
    internal UpdateResult Update()
    {
        sequence++;
        return Engine.Update(Source, sequence, sequence % 2 != 0 ? True : False);
    }
    internal StateDelivery Deliver()
    {
        var result = Update();
        if (result.Status != UpdateStatus.Accepted || !Subscription!.TryRead(out var delivery)) throw new InvalidOperationException("Lost update.");
        if (delivery!.Kind != DeliveryKind.Changed || delivery.RuleIds.Length != 10) throw new InvalidOperationException("Invalid delivery or candidate count.");
        return delivery;
    }
    public void Dispose() { Subscription?.Dispose(); Engine.Dispose(); }
}
