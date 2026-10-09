using System.Threading.Channels;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

/// <summary>In-process composition/lifecycle boundary. No OS observer, service, IPC or persistence.</summary>
public sealed class ControlledRuntimeHost
{
    private readonly object gate = new();
    private readonly EngineHost engine;
    private readonly IRuntimeObserver observer;
    private readonly TimeProvider time;
    private readonly DeadlineScheduler? scheduler;
    private readonly ResourceKey[] resources;
    private sealed class Registration(Mailbox mailbox)
    {
        internal readonly Mailbox Mailbox = mailbox;
        internal volatile bool Active = true;
    }
    private sealed record HeldLease(IObservationLease Lease, Registration Registration, ObservationRequirement? Guarantee);
    private sealed record IngressEvent(long Epoch, Registration Registration, ObserverEvent Event);
    private readonly Dictionary<ResourceKey, HeldLease> held = [];
    private readonly HashSet<ResourceKey> pendingBaselines = [];
    private readonly HashSet<ResourceKey> deferredKeys = [];
    private bool canDrainDeferred;
    private sealed class Mailbox(int capacity)
    {
        internal readonly Channel<IngressEvent> Data = Channel.CreateBounded<IngressEvent>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        internal readonly Queue<IngressEvent> Deferred = [];
        internal int Buffered;
        internal int Gap;
    }
    private Mailbox inbox;
    private Queue<IngressEvent> deferred => inbox.Deferred;
    private long processedPlan;
    private long reportedPlan;
    private Channel<IngressEvent> data => inbox.Data;
    private readonly Channel<bool> wake = SignalChannel();
    private readonly Channel<bool> ingressWake = SignalChannel();
    private TaskCompletionSource started = Signal();
    private TaskCompletionSource cleaned = CompletedSignal();
    private Task coordinator = Task.CompletedTask;
    private Task ingress = Task.CompletedTask;
    private Task plans = Task.CompletedTask;
    private Task faults = Task.CompletedTask;
    private readonly Dictionary<ResourceKey, SourceSession> sources = [];
    private readonly Dictionary<ResourceKey, long> sourceSequences = [];
    private readonly Dictionary<ResourceKey, long> baselineWatermarks = [];
    private long lastSequence = -1;
    private long epoch;
    private bool paused = true;
    private volatile bool stopping;
    private bool active;
    private bool recover;
    private bool recoveryRequired;
    private bool disposed;
    private bool disposing;
    private bool bootstrapped;
    private bool baselineReady;
    private (long Config, long Mode)? blocked;
    private string? error;
    public EngineHost Engine => engine;
    public Task Cleanup { get { lock (gate) return Task.WhenAll(cleaned.Task, coordinator, ingress, plans, faults); } }
    public string? Error { get { lock (gate) return error; } }
    public ResourceKey[] Held { get { lock (gate) return held.Keys.ToArray(); } }
    public int QueueCapacity { get; }
    // Test-only scheduling barriers. Null in normal composition; they do not choose runtime policy.
    internal Func<Task>? BeforeFaultHandling { get; set; }
    internal Func<Task>? BeforeDisposeCommit { get; set; }
    internal Func<Task>? BeforePlanIteration { get; set; }
    internal Action? AfterCallbackAdmission { get; set; }
    internal Func<Task>? BeforeSnapshotAcknowledgment { get; set; }
    internal Func<Task>? AfterIngressBatch { get; set; }
    internal Func<int, Task>? AfterDeferredBatch { get; set; }

    public ControlledRuntimeHost(EngineHost engine, IRuntimeObserver observer, TimeProvider time,
        DeadlineScheduler? ownedScheduler = null, int queueCapacity = 256)
    {
        this.engine = engine; this.observer = observer; this.time = time; scheduler = ownedScheduler;
        if (queueCapacity is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        resources = observer.Resources.ToArray();
        if (resources.Length is < 1 or > 128 || resources.Distinct().Count() != resources.Length)
            throw new ArgumentException("Invalid fixed observer resource set.", nameof(observer));
        QueueCapacity = queueCapacity;
        inbox = new(queueCapacity);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource CompletedSignal() { var signal = Signal(); signal.SetResult(); return signal; }
    private static Channel<bool> SignalChannel() => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });

    public Task StartAsync(Action<EngineHost> configure)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed || disposing, this);
            if (active) throw new InvalidOperationException("Previous lifecycle has not been cleaned up.");
            if (!coordinator.IsCompleted || !ingress.IsCompleted || !plans.IsCompleted || !faults.IsCompleted)
                throw new InvalidOperationException("Previous lifecycle tasks are still exiting.");
            var schedulerFault = scheduler?.BeginLifecycle();
            stopping = false; paused = true; recover = false; recoveryRequired = false; bootstrapped = false; baselineReady = false;
            blocked = null; error = null; Interlocked.Increment(ref epoch);
            Volatile.Write(ref inbox, new Mailbox(QueueCapacity));
            processedPlan = 0;
            sources.Clear(); sourceSequences.Clear(); baselineWatermarks.Clear(); lastSequence = -1;
            ClearBuffered(); pendingBaselines.Clear();
            deferredKeys.Clear(); canDrainDeferred = false;
            while (wake.Reader.TryRead(out _)) { }
            while (ingressWake.Reader.TryRead(out _)) { }
            started = Signal(); cleaned = Signal();
            engine.Start();
            engine.EnableObservationSafety();
            try
            {
                configure(engine);
                foreach (var key in resources) Register(key);
                engine.GetObservationPlan();
            }
            catch { engine.Stop(); cleaned.TrySetResult(); throw; }
            active = true;
            ingress = IngressAsync();
            coordinator = CoordinateAsync();
            plans = WatchPlansAsync();
            faults = WatchFaultAsync(schedulerFault);
            wake.Writer.TryWrite(true);
            return started.Task;
        }
    }

    // Explicit health/reconnect/resume signal. It never polls or invents missed edges.
    public void Recover()
    {
        lock (gate)
        {
            if (!active) throw new InvalidOperationException("Runtime is not active.");
            SynchronizeEngineStop();
            blocked = null;
            if (!stopping)
            {
                paused = true; baselineReady = false; recover = true; Interlocked.Increment(ref epoch);
                Volatile.Write(ref inbox, new Mailbox(QueueCapacity));
                ClearBuffered(); pendingBaselines.Clear();
                deferredKeys.Clear(); canDrainDeferred = false;
                Invalidate();
            }
            wake.Writer.TryWrite(true);
        }
    }

    public async Task<RuntimeStopResult> StopAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        if (budget <= TimeSpan.Zero || budget > DeadlineScheduler.MaximumSlice) throw new ArgumentOutOfRangeException(nameof(budget));
        Task completion;
        lock (gate)
        {
            if (active)
            {
                stopping = true; paused = true; Interlocked.Increment(ref epoch); blocked = null;
                engine.Stop();
                wake.Writer.TryWrite(true); ingressWake.Writer.TryWrite(true);
            }
            completion = Cleanup;
        }
        try { await completion.WaitAsync(budget, time, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return new(false, Held, engine.InFlightExecutions, Error ?? "Stop budget expired; cleanup remains active."); }
        return new(true, Held, engine.InFlightExecutions, Error);
    }

    public async Task DisposeAsync(TimeSpan budget)
    {
        lock (gate) { if (disposed) return; disposing = true; }
        var result = await StopAsync(budget).ConfigureAwait(false);
        if (!result.Completed) throw new InvalidOperationException("Runtime still owns resources; await cleanup before disposal.");
        if (BeforeDisposeCommit is { } beforeDispose) await beforeDispose().ConfigureAwait(false);
        lock (gate) { disposed = true; engine.Dispose(); scheduler?.Dispose(); }
    }

    private void Post(long callbackEpoch, ResourceKey key, Registration registration, ObserverEvent item)
    {
        var mailbox = registration.Mailbox;
        if (stopping || callbackEpoch != Interlocked.Read(ref epoch) || !registration.Active || !ReferenceEquals(mailbox, Volatile.Read(ref inbox))) return;
        AfterCallbackAdmission?.Invoke();
        if (item.Gap || item.Observation is { } observation && observation.Key != key)
            Interlocked.Exchange(ref mailbox.Gap, 1);
        else
        {
            if (Interlocked.Increment(ref mailbox.Buffered) > QueueCapacity || !mailbox.Data.Writer.TryWrite(new(callbackEpoch, registration, item)))
            { Interlocked.Decrement(ref mailbox.Buffered); Interlocked.Exchange(ref mailbox.Gap, 1); }
        }
        ingressWake.Writer.TryWrite(true);
    }

    private async Task IngressAsync()
    {
        while (true)
        {
            await ingressWake.Reader.ReadAsync().ConfigureAwait(false);
            var drained = 0;
            lock (gate)
            {
                SynchronizeEngineStop();
                if (!active || stopping) return;
                if (Interlocked.Exchange(ref inbox.Gap, 0) != 0)
                {
                    paused = true; baselineReady = false; Interlocked.Increment(ref epoch);
                    Invalidate();
                    error = "Observation gap; explicit recovery required.";
                    recoveryRequired = true;
                    var current = engine.GetObservationPlan();
                    blocked = (current.ConfigurationRevision, current.ModeGeneration);
                }
                if (paused) continue; // Keep events newer than the pending snapshot's watermark.
                try { if (canDrainDeferred) drained = DrainDeferred(); }
                catch (Exception exception)
                {
                    error ??= "Observation processing failed: " + exception.Message;
                    SynchronizeEngineStop();
                    if (stopping) return;
                    paused = true; baselineReady = false; recoveryRequired = true; Invalidate();
                    var current = engine.GetObservationPlan(); blocked = (current.ConfigurationRevision, current.ModeGeneration);
                    continue;
                }
                // A bounded batch under the gate prevents an endless producer from starving control.
                for (var i = drained; i < 32 && data.Reader.TryRead(out var entry); i++)
                {
                    if (paused || entry.Epoch != epoch || !entry.Registration.Active || entry.Event.Sequence <= lastSequence)
                    { Interlocked.Decrement(ref inbox.Buffered); continue; }
                    var item = entry.Event;
                    if (lastSequence >= 0 && item.Sequence - lastSequence != 1)
                    {
                        Interlocked.Decrement(ref inbox.Buffered);
                        paused = true; baselineReady = false; Interlocked.Increment(ref epoch); Invalidate(); recoveryRequired = true;
                        error = "Source sequence gap; explicit recovery required.";
                        var current = engine.GetObservationPlan(); blocked = (current.ConfigurationRevision, current.ModeGeneration);
                        break;
                    }
                    lastSequence = item.Sequence;
                    if (item.Observation is { } pending && (pendingBaselines.Contains(pending.Key) || deferredKeys.Contains(pending.Key)))
                    { deferred.Enqueue(entry); deferredKeys.Add(pending.Key); continue; }
                    Interlocked.Decrement(ref inbox.Buffered);
                    if (item.Observation is null || !held.ContainsKey(item.Observation.Key)) continue;
                    if (baselineWatermarks.TryGetValue(item.Observation.Key, out var fence) && item.Sequence <= fence) continue;
                    UpdateResult result;
                    try { result = engine.Update(sources[item.Observation.Key], ++sourceSequences[item.Observation.Key], item.Observation); }
                    catch (Exception exception)
                    {
                        error = "Observation processing failed: " + exception.Message; paused = true; Interlocked.Increment(ref epoch);
                        if (!engine.IsRunning)
                        { stopping = true; wake.Writer.TryWrite(true); return; }
                        Invalidate();
                        break;
                    }
                    if (result.Status is UpdateStatus.Gap or UpdateStatus.SnapshotRequired or UpdateStatus.StaleEpoch)
                    {
                        paused = true; baselineReady = false; Interlocked.Increment(ref epoch); error = "Source sequence gap; explicit recovery required.";
                        Invalidate();
                        recoveryRequired = true;
                        var current = engine.GetObservationPlan(); blocked = (current.ConfigurationRevision, current.ModeGeneration);
                    }
                }
                if (!paused && (data.Reader.TryPeek(out _) || canDrainDeferred && deferred.Count != 0)) ingressWake.Writer.TryWrite(true);
            }
            if (drained != 0 && AfterDeferredBatch is { } afterDeferred) await afterDeferred(drained).ConfigureAwait(false);
            if (AfterIngressBatch is { } afterBatch) await afterBatch().ConfigureAwait(false);
        }
    }

    private async Task WatchPlansAsync()
    {
        long version = 0;
        while (true)
        {
            using var cancel = new CancellationTokenSource();
            var next = engine.WaitForObservationPlanAsync(version, cancel.Token);
            if (await Task.WhenAny(next, cleaned.Task).ConfigureAwait(false) != next)
            { cancel.Cancel(); try { await next.ConfigureAwait(false); } catch (OperationCanceledException) { } return; }
            var plan = await next.ConfigureAwait(false);
            version = plan.Version;
            wake.Writer.TryWrite(true);
        }
    }

    private async Task WatchFaultAsync(Task<Exception>? schedulerFault)
    {
        if (schedulerFault is null) return;
        var completed = await Task.WhenAny(schedulerFault, cleaned.Task).ConfigureAwait(false);
        if (BeforeFaultHandling is { } beforeFault) await beforeFault().ConfigureAwait(false);
        // Cleanup can win the queued continuation even when this lifecycle's fault is ready.
        if (completed != schedulerFault && !schedulerFault.IsCompleted) return;
        lock (gate)
        {
            error = "Scheduler fault: " + schedulerFault.Result.Message;
            stopping = true; paused = true; Interlocked.Increment(ref epoch); engine.Stop();
            started.TrySetException(schedulerFault.Result);
            wake.Writer.TryWrite(true); ingressWake.Writer.TryWrite(true);
        }
    }

    private async Task CoordinateAsync()
    {
        while (true)
        {
            await wake.Reader.ReadAsync().ConfigureAwait(false);
            if (BeforePlanIteration is { } beforePlan) await beforePlan().ConfigureAwait(false);
            ObservationPlan plan;
            bool recovery;
            long operationEpoch;
            lock (gate)
            {
                SynchronizeEngineStop();
                plan = engine.GetObservationPlan();
                if (blocked == (plan.ConfigurationRevision, plan.ModeGeneration)) continue;
                if (processedPlan == plan.Version && !recover && !stopping) continue;
                recovery = recover || recoveryRequired; recover = false; recoveryRequired = false;
                if (recovery && !stopping)
                {
                    // Every recovery path, including a new configuration after gap, owns a new mailbox.
                    Interlocked.Increment(ref epoch);
                    Volatile.Write(ref inbox, new Mailbox(QueueCapacity));
                    deferredKeys.Clear(); canDrainDeferred = false;
                }
                operationEpoch = epoch;
            }
            try
            {
                var completeStop = false;
                var snapshotKeys = new List<ResourceKey>();
                var releaseDeferred = false;
                foreach (var key in Held)
                {
                    if (!recovery && plan.References.ContainsKey(key)) continue;
                    HeldLease lease;
                    lock (gate)
                    {
                        SynchronizeEngineStop();
                        if (!engine.TryBeginObservationRelease(key, recovery))
                        { releaseDeferred |= recovery; continue; }
                        lease = held[key]; lease.Registration.Active = false;
                        engine.TryDisconnect(sources[key]);
                    }
                    await lease.Lease.ReleaseAsync().ConfigureAwait(false);
                    lock (gate)
                    {
                        held.Remove(key); pendingBaselines.Remove(key);
                        SynchronizeEngineStop();
                    }
                }
                if (releaseDeferred)
                {
                    lock (gate) recover = true;
                    await engine.WaitForIdleAsync().ConfigureAwait(false);
                    wake.Writer.TryWrite(true);
                    continue;
                }
                if (!stopping)
                {
                    foreach (var key in plan.References.Keys)
                    {
                        lock (gate) { if (held.ContainsKey(key)) continue; }
                        if (!resources.Contains(key)) throw new InvalidOperationException("Demand outside fixed source resources.");
                        var registration = new Registration(inbox);
                        lock (gate) { pendingBaselines.Add(key); canDrainDeferred = false; }
                        var lease = await observer.AcquireAsync(key, item => Post(operationEpoch, key, registration, item)).ConfigureAwait(false);
                        lock (gate) held.Add(key, new(lease, registration, null));
                        // External getters run outside the host gate; cache their immutable promise.
                        var guarantee = lease.Guarantee;
                        lock (gate) held[key] = held[key] with { Guarantee = guarantee };
                        if (stopping) break;
                    }
                    lock (gate) snapshotKeys.AddRange(pendingBaselines.Where(key => held.ContainsKey(key) && plan.References.ContainsKey(key)));
                    if (!stopping && (snapshotKeys.Count != 0 || !bootstrapped && !baselineReady))
                    {
                        lock (gate)
                        {
                            if (!GuaranteesSatisfied(plan)) throw new InvalidOperationException("Observer guarantee weaker than required.");
                            foreach (var key in snapshotKeys) Register(key);
                            if (!bootstrapped || recovery) { paused = true; baselineReady = false; }
                        }
                        var snapshot = await observer.SnapshotAsync(snapshotKeys).ConfigureAwait(false);
                        lock (gate)
                        {
                            SynchronizeEngineStop();
                            if (!stopping && epoch == operationEpoch && Volatile.Read(ref inbox.Gap) == 0)
                            {
                                var observations = snapshot.Observations.ToDictionary(o => o.Key);
                                if (!snapshotKeys.ToHashSet().SetEquals(observations.Keys)) throw new InvalidOperationException("Incomplete active snapshot.");
                                var latest = engine.GetObservationPlan();
                                if (!GuaranteesSatisfied(latest)) throw new InvalidOperationException("Observer guarantee weaker than current requirement.");
                                var guarantees = Guarantees();
                                foreach (var key in snapshotKeys)
                                {
                                    latest = engine.GetObservationPlan();
                                    if (!latest.References.ContainsKey(key)) continue;
                                    if (!engine.TryApplyObservationSnapshot(latest.Version, sources[key], sourceSequences[key] + 1, observations[key], guarantees))
                                        throw new InvalidOperationException("Snapshot rejected by current observation plan.");
                                    sourceSequences[key]++;
                                    baselineWatermarks[key] = snapshot.Sequence;
                                    pendingBaselines.Remove(key);
                                    // A snapshot is not yet a physical-plan acknowledgment. Keep
                                    // subsequent edges deferred until resumed rules are admitted.
                                    deferredKeys.Add(key);
                                }
                                if (!bootstrapped || recovery) lastSequence = snapshot.Sequence;
                                baselineReady = true;
                            }
                            else if (!stopping) throw new InvalidOperationException("Snapshot interrupted by gap or recovery.");
                        }
                    }
                }
                if (snapshotKeys.Count != 0 && BeforeSnapshotAcknowledgment is { } beforeAcknowledgment) await beforeAcknowledgment().ConfigureAwait(false);
                lock (gate)
                {
                    SynchronizeEngineStop();
                    var guarantees = Guarantees();
                    var valid = held.Keys.ToHashSet().SetEquals(plan.References.Keys) && GuaranteesSatisfied(plan);
                    var acknowledged = valid;
                    if (plan.Version > reportedPlan)
                    {
                        acknowledged = engine.AcknowledgeObservationPlan(plan.Version, held.Keys, guarantees: guarantees);
                        reportedPlan = plan.Version;
                    }
                    processedPlan = plan.Version;
                    if (stopping)
                    {
                        if (held.Count == 0 && engine.InFlightExecutions == 0)
                        {
                            completeStop = true;
                        }
                    }
                    else if (plan.Version == engine.GetObservationPlan().Version && !valid)
                        throw new InvalidOperationException("Observer guarantees or resources were not acknowledged.");
                    else if (acknowledged && baselineReady && plan.Version == engine.GetObservationPlan().Version)
                    {
                        if (!bootstrapped) { engine.StartExecution(); bootstrapped = true; started.TrySetResult(); }
                        paused = false; canDrainDeferred = pendingBaselines.Count == 0; ingressWake.Writer.TryWrite(true);
                        error = null;
                    }
                }
                if (completeStop)
                {
                    await engine.StopAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
                    lock (gate)
                    {
                        active = false; started.TrySetCanceled(); cleaned.TrySetResult();
                        ingressWake.Writer.TryWrite(true);
                    }
                    return;
                }
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    SynchronizeEngineStop();
                    error ??= exception.Message;
                    if (!stopping) { paused = true; baselineReady = false; recoveryRequired = true; Invalidate(); }
                    engine.AcknowledgeObservationPlan(plan.Version, held.Keys, error);
                    if (!bootstrapped && !stopping)
                    {
                        started.TrySetException(exception); stopping = true; paused = true; Interlocked.Increment(ref epoch);
                        engine.Stop(); ingressWake.Writer.TryWrite(true); wake.Writer.TryWrite(true);
                    }
                    else
                    {
                        var current = engine.GetObservationPlan();
                        if (recover) { blocked = null; wake.Writer.TryWrite(true); }
                        else blocked = (current.ConfigurationRevision, current.ModeGeneration);
                    }
                }
            }
        }
    }

    private void Register(ResourceKey key)
    {
        var index = Array.IndexOf(resources, key);
        sources[key] = engine.RegisterSource(observer.SourceId + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), [key]);
        sourceSequences[key] = -1;
    }
    private void Invalidate()
    {
        foreach (var session in sources.Values) engine.TryDisconnect(session);
    }
    private bool GuaranteesSatisfied(ObservationPlan plan) => plan.Requirements.All(p =>
        held.TryGetValue(p.Key, out var lease) && lease.Guarantee is { } guarantee && guarantee.MaximumDelay >= TimeSpan.Zero &&
        guarantee.MaximumDelay <= p.Value.MaximumDelay && (!p.Value.PreserveEdges || guarantee.PreserveEdges));
    private Dictionary<ResourceKey, ObservationRequirement> Guarantees() => held.Where(p => p.Value.Guarantee is not null)
        .ToDictionary(p => p.Key, p => p.Value.Guarantee!);

    private void SynchronizeEngineStop()
    {
        if (stopping || engine.IsRunning) return;
        stopping = true; paused = true; blocked = null; Interlocked.Increment(ref epoch);
        error ??= engine.RuntimeFault;
        ingressWake.Writer.TryWrite(true); wake.Writer.TryWrite(true);
    }
    private void ClearBuffered()
    {
        while (data.Reader.TryRead(out _)) Interlocked.Decrement(ref inbox.Buffered);
        while (deferred.TryDequeue(out _)) Interlocked.Decrement(ref inbox.Buffered);
    }
    private int DrainDeferred()
    {
        var count = Math.Min(32, deferred.Count);
        for (var i = 0; i < count; i++)
        {
            var entry = deferred.Dequeue();
            if (entry.Epoch == epoch && entry.Registration.Active && entry.Event.Observation is { } observation && held.ContainsKey(observation.Key))
            {
                if (pendingBaselines.Contains(observation.Key)) { deferred.Enqueue(entry); continue; }
                if (!baselineWatermarks.TryGetValue(observation.Key, out var fence) || entry.Event.Sequence > fence)
                    engine.Update(sources[observation.Key], ++sourceSequences[observation.Key], observation);
            }
            Interlocked.Decrement(ref inbox.Buffered);
        }
        if (deferred.Count == 0) deferredKeys.Clear();
        return count;
    }
}
