using System.Threading.Channels;
using Axiom.Domain;
using Axiom.Infrastructure;
using static Axiom.Engine.Tests.AutomationFixture;

namespace Axiom.Engine.Tests;

internal sealed class RuntimeObserver : IRuntimeObserver
{
    internal readonly Dictionary<ResourceKey, bool> Values = new() { [Input] = false, [Output] = false, [Noise] = false };
    internal readonly Dictionary<ResourceKey, Lease> Leases = [];
    internal readonly List<Lease> History = [];
    internal readonly Channel<ResourceKey> Acquired = Channel.CreateUnbounded<ResourceKey>();
    internal readonly Channel<ResourceKey> Released = Channel.CreateUnbounded<ResourceKey>();
    internal readonly Channel<bool> Snapshots = Channel.CreateUnbounded<bool>();
    internal Func<ResourceKey, Task>? BeforeAcquire;
    internal Func<ResourceKey, Task>? BeforeRelease;
    internal Func<Task>? BeforeSnapshot;
    internal bool Weak;
    internal TimeSpan? MaximumDelay;
    internal Action? ReadGuarantee;
    internal long Sequence;
    public string SourceId => "controlled";
    public IReadOnlyCollection<ResourceKey> Resources => [Input, Output, Noise];
    public async ValueTask<IObservationLease> AcquireAsync(ResourceKey key, Action<ObserverEvent> callback)
    {
        if (BeforeAcquire is { } before) await before(key);
        var lease = new Lease(this, key, callback, Weak);
        lock (Leases) { Assert.False(Leases.ContainsKey(key)); Leases.Add(key, lease); History.Add(lease); }
        Acquired.Writer.TryWrite(key);
        return lease;
    }
    public async ValueTask<ObserverSnapshot> SnapshotAsync(IReadOnlyCollection<ResourceKey> activeResources)
    {
        // Capture a consistent watermark before the test allows concurrent notifications.
        ObserverSnapshot snapshot;
        lock (Leases) snapshot = new(Sequence, activeResources.Select(key => Observe(key, Values[key])).ToArray());
        Snapshots.Writer.TryWrite(true);
        if (BeforeSnapshot is { } before) await before();
        return snapshot;
    }
    internal void Emit(ResourceKey key, bool value)
    {
        lock (Leases) { Values[key] = value; Leases[key].Callback(new(++Sequence, Observe(key, value))); }
    }
    internal void Gap()
    {
        lock (Leases) Leases.Values.First().Callback(new(Sequence, null, true));
    }
    internal sealed class Lease(RuntimeObserver owner, ResourceKey key, Action<ObserverEvent> callback, bool weak) : IObservationLease
    {
        internal Action<ObserverEvent> Callback = callback;
        public ObservationRequirement Guarantee
        {
            get { owner.ReadGuarantee?.Invoke(); return new(owner.MaximumDelay ?? TimeSpan.FromMilliseconds(weak ? 2000 : 0), !weak); }
        }
        public async ValueTask ReleaseAsync()
        {
            if (owner.BeforeRelease is { } before) await before(key);
            lock (owner.Leases) Assert.True(owner.Leases.Remove(key));
            owner.Released.Writer.TryWrite(key);
        }
    }
}

internal sealed class RuntimeFixture
{
    internal readonly ManualTimeProvider Time = new();
    internal readonly RuntimeClock Clock;
    internal readonly DeadlineScheduler Scheduler;
    internal readonly ControlledExecutor Executor = new();
    internal readonly RuntimeObserver Observer = new();
    internal readonly EngineHost Engine;
    internal readonly ControlledRuntimeHost Host;
    private readonly SequentialIds ids = new();
    internal RuntimeFixture(int capacity = 256)
    {
        Clock = new(Time); Scheduler = new(Clock); Engine = new(Clock, Scheduler, Executor);
        Host = new(Engine, Observer, Time, Scheduler, capacity);
    }
    internal RuleDefinition Rule(string id = "rule", bool command = false, bool keep = true, long version = 1) =>
        new(id, version, "owner", ResourceScope.Machine, RuleCondition.Equal(Input, StateValue.From(Schema, true)),
            command ? RuleAction.Command(Output, StateValue.From(Schema, true)) : RuleAction.Desired(Output, StateValue.From(Schema, true)),
            retention: new(keep));
    internal Task Start(bool command = false, bool keep = true, bool cancellable = true) => Host.StartAsync(e =>
    {
        e.ConfigureAutomation(new FakeAuthorization(), ids);
        e.RegisterCapability(Binding("input")); e.RegisterCapability(Binding("output", cancellable: cancellable));
        e.SetRule(Rule(command: command, keep: keep));
    });
    internal static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));
    internal static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));
    internal static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal async Task Stop()
    {
        var result = await Within(Host.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.True(result.Completed, result.Error);
        Assert.Empty(result.Held);
        Assert.Equal(0, Scheduler.ActiveTimers);
    }
}
