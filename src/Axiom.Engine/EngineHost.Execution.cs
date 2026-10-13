using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Engine;

public sealed record ExecutionLimits
{
    public int Concurrency { get; }
    public int QueueCapacity { get; }
    public int MaximumAttempts { get; }
    public TimeSpan RetryBase { get; }
    public TimeSpan RetryCap { get; }
    public ExecutionLimits(int concurrency = 4, int queueCapacity = 256, int maximumAttempts = 3,
        TimeSpan? retryBase = null, TimeSpan? retryCap = null)
    {
        if (concurrency is < 1 or > AutomationTaskLimits.MaximumTasks || queueCapacity is < 1 or > 4096 || maximumAttempts is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(concurrency));
        (Concurrency, QueueCapacity, MaximumAttempts) = (concurrency, queueCapacity, maximumAttempts);
        RetryBase = retryBase ?? TimeSpan.FromSeconds(1); RetryCap = retryCap ?? TimeSpan.FromSeconds(30);
        if (RetryBase <= TimeSpan.Zero || RetryCap < RetryBase) throw new ArgumentOutOfRangeException(nameof(retryBase));
    }
}

public sealed partial class EngineHost
{
    private sealed record Work(ResourceKey Key, long Generation, RuleAction Action, string Principal, Guid Id,
        CapabilityDescriptor Capability, ImmutableArray<string> Winners, bool Manual = false, DateTimeOffset? Deadline = null,
        ImmutableArray<ResourceKey> RequiredResources = default, int Priority = 0);
    private sealed class Flight(Work work, long observedVersion)
    {
        internal Work Work = work;
        internal long ObservedVersion = observedVersion;
        internal CancellationTokenSource Cancellation = new();
        internal Task? CancellationTask;
        internal bool TimedOut;
        internal DateTimeOffset UtcDeadline;
        internal Guid? ReceiptId;
        internal TaskCompletionSource<Outcome> FinalReceipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Outcome> FirstReceipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ExecutionState(long generation)
    {
        internal long Generation = generation;
        internal int Attempts;
        internal long? ObservationFence;
        internal bool AwaitingReceiptObservation;
        internal bool Blocked;
        internal TimeSpan? RetryAt;
        internal TimeSpan? LastApply;
        internal Outcome? Outcome;
    }
    private sealed class ExecutionSession
    {
        internal bool Active = true;
        internal TaskCompletionSource Start = NewSignal();
        internal Task[] Workers = [];
    }
    private ExecutionLimits executionLimits = new();
    private ExecutionSession? executionSession;
    private readonly Dictionary<ResourceKey, Flight> flights = [];
    private readonly Dictionary<ResourceKey, ExecutionState> executionStates = [];
    private readonly LinkedList<Work> pendingWork = new();
    private readonly Dictionary<ResourceKey, LinkedListNode<Work>> pendingByKey = [];
    private readonly HashSet<Guid> acceptedCommands = [];
    private TaskCompletionSource workSignal = NewSignal();
    private TaskCompletionSource idleSignal = CompletedSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource CompletedSignal() { var signal = NewSignal(); signal.SetResult(); return signal; }
    public int PendingExecutions { get { lock (gate) return pendingWork.Count; } }
    public int InFlightExecutions { get { lock (gate) return flights.Count; } }

    // Explicitly start bounded, event-driven workers after configuration/bootstrap snapshots.
    public void StartExecution(ExecutionLimits? limits = null)
    {
        lock (gate)
        {
            RequireAutomation();
            if (executionSession is { Active: true }) return;
            if (flights.Count != 0) throw new InvalidOperationException("Previous execution still in flight.");
            executionLimits = limits ?? new();
            var session = new ExecutionSession();
            executionSession = session;
            session.Workers = Enumerable.Range(0, executionLimits.Concurrency).Select(_ => WorkerAsync(session)).ToArray();
            foreach (var key in targetRules.Keys.ToArray()) Recompute(key);
            session.Start.SetResult();
        }
    }
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        lock (gate) return idleSignal.Task.WaitAsync(cancellationToken);
    }
    public async Task StopAndWaitAsync(CancellationToken cancellationToken)
    {
        Task[] workers;
        lock (gate) { Stop(); workers = executionSession?.Workers ?? []; }
        await Task.WhenAll(workers).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private void SignalWork()
    {
        var previous = workSignal; workSignal = NewSignal(); previous.TrySetResult();
        if (pendingWork.Count == 0 && flights.Count == 0) idleSignal.TrySetResult();
        else if (idleSignal.Task.IsCompleted) idleSignal = NewSignal();
    }
    private void RemovePending(ResourceKey key)
    {
        if (pendingByKey.Remove(key, out var node)) pendingWork.Remove(node);
        CancelDeadline(("queued", key));
    }
    private void RequestCancellation(Flight flight)
    {
        if (flight.CancellationTask is null && flight.Work.Capability.Cancellable)
            flight.CancellationTask = flight.Cancellation.CancelAsync(); // callbacks never run under Engine gate
    }
    private void SetExecutionStatus(ResourceKey key, DecisionStatus status, string reason, Outcome? outcome = null)
    {
        if (decisions.TryGetValue(key, out var decision)) decisions[key] = decision with { Status = status, Reason = reason, LastOutcome = outcome ?? decision.LastOutcome };
    }
    partial void ReconcileDecision(ResourceKey key)
    {
        var decision = decisions[key];
        if (Executor is ILeasedDesiredExecutor leases)
            leases.SetDesiredAuthorization(key, decision.Generation,
                executionSession is { Active: true } && decision.Action?.Kind == RuleActionKind.Desired &&
                store.Get(key)?.Observation.Quality == StateQuality.Known &&
                ObservationInputsReady(decision.Winners.SelectMany(id => rules[id].Definition.Condition.Dependencies).Append(key)));
        var edge = decision.Winners.Any(id => rules.TryGetValue(id, out var rule) && rule.CommandEdge);
        if (targetRules.TryGetValue(key, out var owners))
            foreach (var id in owners) rules[id].CommandEdge = false; // suppressed/busy edges are never replayed
        if (executionSession is not { Active: true }) return;
        if (pendingByKey.TryGetValue(key, out var manual) && manual.Value.Manual)
        {
            if (ValidWork(manual.Value)) { SetExecutionStatus(key, DecisionStatus.Queued, "Manual command queued under override"); return; }
            RemovePending(key);
        }
        if (!executionStates.TryGetValue(key, out var state)) executionStates[key] = state = new(decision.Generation);
        if (state.Generation != decision.Generation)
        {
            var replacement = new ExecutionState(decision.Generation) { LastApply = state.LastApply, ObservationFence = state.ObservationFence };
            executionStates[key] = state = replacement;
            CancelDeadline(("retry", key)); CancelDeadline(("apply", key)); CancelDeadline(("confirm", key)); RemovePending(key);
        }
        if (decision.Action is null)
        {
            RemovePending(key); CancelDeadline(("retry", key)); CancelDeadline(("apply", key)); CancelDeadline(("confirm", key));
            if (flights.TryGetValue(key, out var old) && !old.Work.Manual) RequestCancellation(old);
            SignalWork(); return;
        }
        if (flights.TryGetValue(key, out var active))
        {
            if (active.Work.Generation != decision.Generation && !active.Work.Manual) RequestCancellation(active);
            SetExecutionStatus(key, active.TimedOut ? DecisionStatus.Unknown : DecisionStatus.Running,
                active.TimedOut ? "Timeout; real call still owns resource" : "SingleInFlight; new edges discarded");
            return;
        }
        if (!ObservationInputsReady(decision.Winners.SelectMany(id => rules[id].Definition.Condition.Dependencies).Append(key)))
        {
            RemovePending(key); SetExecutionStatus(key, DecisionStatus.AwaitingObservation, "Observation lease releasing or guarantee unconfirmed");
            SignalWork(); return;
        }
        if (pendingByKey.TryGetValue(key, out var queued) && queued.Value.Generation == decision.Generation)
        { SetExecutionStatus(key, DecisionStatus.Queued, "Queued"); return; }
        if (decision.Action.Kind == RuleActionKind.Command)
        {
            if (!edge)
            {
                if (state.Outcome is { } outcome) SetExecutionStatus(key, outcome.Status == OutcomeStatus.Succeeded ? DecisionStatus.Confirmed : DecisionStatus.Unknown, "Command receipt only", outcome);
                return;
            }
        }
        else
        {
            var observed = store.Get(key);
            if (observed?.Observation.Quality != StateQuality.Known ||
                state.ObservationFence is { } fence && observationVersions.GetValueOrDefault(key) <= fence)
            { SetExecutionStatus(key, DecisionStatus.AwaitingObservation, "Need fresh Known observation"); return; }
            if (observed.Observation.Value == decision.Action.Value)
            {
                state.ObservationFence = null; state.AwaitingReceiptObservation = false; state.Attempts = 0; state.RetryAt = null;
                CancelDeadline(("retry", key)); CancelDeadline(("apply", key)); CancelDeadline(("confirm", key));
                SetExecutionStatus(key, DecisionStatus.Confirmed, "Observed target satisfied", state.Outcome); return;
            }
            if (state.AwaitingReceiptObservation)
            {
                state.AwaitingReceiptObservation = false;
                CancelDeadline(("confirm", key));
                if (state.Attempts >= executionLimits.MaximumAttempts || capabilities[key.CapabilityId].Descriptor.Retry == RetryCategory.Never) state.Blocked = true;
                else ScheduleRetry(key, state);
            }
            if (state.Blocked) { SetExecutionStatus(key, DecisionStatus.Blocked, "Permanent failure or attempts exhausted", state.Outcome); return; }
            if (state.Attempts >= executionLimits.MaximumAttempts)
            { state.Blocked = true; SetExecutionStatus(key, DecisionStatus.Blocked, "Attempts exhausted; await configuration/health change", state.Outcome); return; }
            if (state.RetryAt is { } retryAt && clock.MonotonicNow < retryAt)
            { SetExecutionStatus(key, DecisionStatus.Blocked, "CappedBackoff", state.Outcome); return; }
            var descriptor = capabilities[key.CapabilityId].Descriptor;
            if (state.LastApply is { } last && clock.MonotonicNow < last + descriptor.MinimumApplyInterval)
            {
                SetDeadline(("apply", key), last + descriptor.MinimumApplyInterval, () => Recompute(key));
                SetExecutionStatus(key, DecisionStatus.Blocked, "MinimumApplyInterval"); return;
            }
        }
        var winner = rules[decision.Winners[0]].Definition;
        var descriptorForWork = capabilities[key.CapabilityId].Descriptor;
        Guid requestId;
        try { requestId = commandIds!.NextId(); }
        catch (Exception) { SetExecutionStatus(key, DecisionStatus.Blocked, "CommandIdSourceFailed"); return; }
        if (requestId == Guid.Empty) { SetExecutionStatus(key, DecisionStatus.Blocked, "CommandIdSourceReturnedEmpty"); return; }
        var work = new Work(key, decision.Generation, decision.Action, winner.Principal, requestId, descriptorForWork, decision.Winners,
            Deadline: decision.Action.Kind == RuleActionKind.Command ? clock.UtcNow + descriptorForWork.Timeout : null,
            RequiredResources: ExpandResources(decision.Winners.SelectMany(id => rules[id].Definition.Condition.Dependencies).Append(key)).ToImmutableArray(), Priority: winner.Priority);
        if (!Enqueue(work)) return;
        if (work.Action.Kind == RuleActionKind.Command)
            foreach (var id in work.Winners) rules[id].LastTriggered = clock.MonotonicNow;
    }
    private bool Enqueue(Work work)
    {
        if (!ObservationInputsReady(work.RequiredResources.IsDefault ? [work.Key] : work.RequiredResources)) return false;
        if (work.Action.Kind == RuleActionKind.Command && (acceptedCommands.Contains(work.Id) || acceptedCommands.Count >= 65_536))
        { SetExecutionStatus(work.Key, DecisionStatus.Blocked, "DuplicateCommandId or command receipt capacity exceeded"); return false; }
        if (pendingWork.Count >= executionLimits.QueueCapacity)
        { SetExecutionStatus(work.Key, DecisionStatus.Blocked, "QueueCapacityExceeded; request not accepted"); return false; }
        RemovePending(work.Key);
        pendingByKey[work.Key] = pendingWork.AddLast(work);
        if (work.Action.Kind == RuleActionKind.Command) acceptedCommands.Add(work.Id);
        if (work.Deadline is { } until)
            SetDeadline(("queued", work.Key), clock.MonotonicNow + (until - clock.UtcNow), () =>
            {
                if (pendingByKey.TryGetValue(work.Key, out var queued) && queued.Value.Id == work.Id)
                { RemovePending(work.Key); SetExecutionStatus(work.Key, DecisionStatus.Failed, "Queued command deadline expired"); SignalWork(); }
            });
        SetExecutionStatus(work.Key, DecisionStatus.Queued, "Queued"); SignalWork(); return true;
    }
    private bool ValidWork(Work work)
    {
        if (!ObservationInputsReady(work.RequiredResources.IsDefault ? [work.Key] : work.RequiredResources)) return false;
        if (!running || authorization is null || !capabilities.TryGetValue(work.Key.CapabilityId, out var binding) ||
            !binding.Descriptor.Available || !binding.Accepts(work.Action.Value) || binding.Descriptor.Scope != work.Key.Scope.Kind ||
            !Authorize(work.Principal, work.Key, Operation(work.Action)).Allowed || work.Deadline <= clock.UtcNow ||
            work.Action.Kind == RuleActionKind.Command && !binding.Descriptor.Command || work.Action.Kind == RuleActionKind.Desired && !binding.Descriptor.DesiredState) return false;
        if (work.Manual) return overrides.TryGetValue(work.Key, out var item) && item.Owner == work.Principal;
        if (!decisions.TryGetValue(work.Key, out var decision) || decision.Generation != work.Generation || decision.Action != work.Action || overrides.ContainsKey(work.Key)) return false;
        if (work.Action.Kind == RuleActionKind.Desired)
        {
            var observed = store.Get(work.Key)?.Observation;
            if (observed?.Quality != StateQuality.Known || observed.Value == work.Action.Value) return false;
            if (executionStates.GetValueOrDefault(work.Key)?.ObservationFence is { } fence && observationVersions.GetValueOrDefault(work.Key) <= fence) return false;
        }
        return work.Winners.All(id => rules.TryGetValue(id, out var rule) && rule.Value == TruthValue.True && Eligibility(rule) is null && WithinTimeWindow(rule) &&
            (rule.Definition.Timing.MaximumInputAge is not { } age || rule.Definition.Condition.Dependencies.All(key =>
                receivedAt.TryGetValue(key, out var at) && clock.MonotonicNow - at < age)));
    }
    private Flight? TakeWork()
    {
        for (var node = pendingWork.First; node is not null;)
        {
            var next = node.Next;
            var work = node.Value;
            if (!ValidWork(work))
            { RemovePending(work.Key); SetExecutionStatus(work.Key, DecisionStatus.Blocked,
                work.Winners.Any(id => rules.TryGetValue(id, out var rule) && !WithinTimeWindow(rule))
                    ? "OutsideTimeWindow; queued command discarded" : "PreExecutionValidationFailed"); node = next; continue; }
            if (flights.ContainsKey(work.Key) || work.Capability.Concurrency == ConcurrencyScope.Capability && flights.Values.Any(f => f.Work.Key.CapabilityId == work.Key.CapabilityId))
            { node = next; continue; }
            RemovePending(work.Key);
            var flight = new Flight(work, observationVersions.GetValueOrDefault(work.Key));
            flight.UtcDeadline = work.Deadline ?? clock.UtcNow + work.Capability.Timeout;
            flights.Add(work.Key, flight);
            if (!executionStates.TryGetValue(work.Key, out var state)) executionStates[work.Key] = state = new(work.Generation);
            state.Attempts++; state.LastApply = clock.MonotonicNow; state.RetryAt = null;
            SetExecutionStatus(work.Key, DecisionStatus.Running, "Executing");
            try { SetDeadline(("timeout", work.Key), clock.MonotonicNow + (flight.UtcDeadline - clock.UtcNow), () =>
            {
                if (!flights.TryGetValue(work.Key, out var current) || !ReferenceEquals(current, flight)) return;
                flight.TimedOut = true; RequestCancellation(flight);
                SetExecutionStatus(work.Key, DecisionStatus.Unknown, "Timeout; side effects unknown; real call still owns resource");
            }); }
            catch when (RuntimeFault is not null)
            {
                // Dispatch never reached the executor: do not leave a phantom physical reservation.
                flights.Remove(work.Key);
                flight.Cancellation.Dispose();
                SignalWork(); DemandChanged();
                return null;
            }
            DemandChanged();
            return flight;
        }
        return null;
    }
    private async Task WorkerAsync(ExecutionSession session)
    {
        await session.Start.Task.ConfigureAwait(false);
        while (true)
        {
            Flight? flight;
            Task wait;
            lock (gate)
            {
                if (!session.Active) return;
                try { flight = TakeWork(); }
                catch when (RuntimeFault is not null && !running)
                { SignalIdle(); return; } // Dequeue rearming failed before any executor call.
                wait = workSignal.Task;
                if (flight is null) SignalIdle();
                if (!session.Active) return;
            }
            if (flight is null) { await wait.ConfigureAwait(false); continue; }
            Outcome outcome;
            var work = flight.Work;
            try
            {
                flight.Cancellation.Token.ThrowIfCancellationRequested();
                outcome = work.Action.Kind == RuleActionKind.Command
                    ? await Executor.ExecuteCommandAsync(new(work.Id, work.Key, work.Action.Value, work.Principal,
                        flight.UtcDeadline, work.Action.IsIdempotent), flight.Cancellation.Token).ConfigureAwait(false)
                    : await Executor.ApplyDesiredAsync(new(work.Key, work.Action.Value, work.Manual ? work.Principal : string.Join("+", work.Winners),
                        work.Priority, work.Generation, "while current generation remains authorized"), flight.Cancellation.Token).ConfigureAwait(false);
                if (work.Action.Kind == RuleActionKind.Command && outcome.CorrelationId != work.Id)
                    outcome = new(OutcomeStatus.Unknown, "Mismatched command correlation", work.Id);
                if (outcome.Status is OutcomeStatus.Accepted or OutcomeStatus.Pending)
                {
                    lock (gate)
                    {
                        flight.ReceiptId = outcome.CorrelationId;
                        SetExecutionStatus(work.Key, DecisionStatus.Running, "Pending receipt; physical reservation retained", outcome);
                        flight.FirstReceipt.TrySetResult(outcome);
                    }
                    outcome = await flight.FinalReceipt.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { outcome = new(OutcomeStatus.Unknown, "Cancelled; side effects unknown", work.Id); }
            catch (Exception exception) { outcome = new(OutcomeStatus.Unknown, $"Executor exception: {exception.GetType().Name}", work.Id); }
            flight.FirstReceipt.TrySetResult(outcome);
            Task? cancellation;
            lock (gate)
            {
                flights.Remove(work.Key);
                cancellation = flight.CancellationTask;
                try
                {
                    CancelDeadline(("timeout", work.Key));
                    if (session.Active && running && authorization is not null) CompleteWork(flight, outcome);
                }
                catch when (RuntimeFault is not null && !running)
                { /* Scheduling already stopped Engine and preserved the cause. The physical call has ended. */ }
                finally { SignalWork(); DemandChanged(); }
            }
            if (cancellation is not null) { try { await cancellation.ConfigureAwait(false); } catch (Exception) { /* Cancellation failure cannot imply rollback. */ } }
            flight.Cancellation.Dispose();
        }
    }
    private void SignalIdle() { if (pendingWork.Count == 0 && flights.Count == 0) idleSignal.TrySetResult(); }
    private void CompleteWork(Flight flight, Outcome outcome)
    {
        var work = flight.Work;
        if (!decisions.TryGetValue(work.Key, out var decision)) return;
        if (!executionStates.TryGetValue(work.Key, out var state)) executionStates[work.Key] = state = new(decision.Generation);
        state.Outcome = outcome;
        decisions[work.Key] = decision with { LastOutcome = outcome };
        var consumedEvent = false;
        if (work.Action.Kind == RuleActionKind.Command && decision.Generation == work.Generation)
            foreach (var id in work.Winners)
                if (rules.TryGetValue(id, out var rule) && rule.Definition.Condition.Kind == ConditionKind.Event)
                { rule.EventLatched = false; rule.Value = TruthValue.False; rule.TrueSince = null; consumedEvent = true; }
        if (work.Manual || decision.Generation != work.Generation || flight.TimedOut || outcome.Status == OutcomeStatus.Unknown)
        {
            // A removable request executor closes the old owned lease before returning.
            // Its latest ownership sample is usable even if it arrived before this old receipt;
            // otherwise a remaining winner can wait forever for a second unchanged false sample.
            state.ObservationFence = Executor is ILeasedDesiredExecutor leaseExecutor && work.Action.Kind == RuleActionKind.Desired &&
                decision.Generation != work.Generation && store.Get(work.Key)?.Observation is { Quality: StateQuality.Known } released &&
                released.Value is { } value && leaseExecutor.IsReleasedObservation(work.Key, value)
                ? null : observationVersions.GetValueOrDefault(work.Key);
            state.AwaitingReceiptObservation = false;
            Recompute(work.Key); return;
        }
        if (work.Action.Kind == RuleActionKind.Command)
        {
            SetExecutionStatus(work.Key, outcome.Status == OutcomeStatus.Succeeded ? DecisionStatus.Confirmed : DecisionStatus.Unknown, "Command receipt; never automatic retry", outcome);
            if (consumedEvent) Recompute(work.Key);
            return;
        }
        if (outcome.Status is OutcomeStatus.Succeeded or OutcomeStatus.Accepted or OutcomeStatus.Pending)
        {
            state.ObservationFence = flight.ObservedVersion;
            state.AwaitingReceiptObservation = true;
            SetDeadline(("confirm", work.Key), clock.MonotonicNow + work.Capability.Timeout, () =>
            {
                if (!executionStates.TryGetValue(work.Key, out var current) || !ReferenceEquals(current, state)) return;
                state.AwaitingReceiptObservation = false;
                state.ObservationFence = observationVersions.GetValueOrDefault(work.Key);
                SetExecutionStatus(work.Key, DecisionStatus.Unknown, "Observation confirmation timeout; wait for fresh observation");
            });
            Recompute(work.Key); return;
        }
        if (outcome.Failure == FailureKind.Transient && work.Capability.Retry == RetryCategory.IdempotentTransientOnly && state.Attempts < executionLimits.MaximumAttempts)
        {
            ScheduleRetry(work.Key, state);
            SetExecutionStatus(work.Key, DecisionStatus.Blocked, "CappedBackoff", outcome);
        }
        else { state.Blocked = true; SetExecutionStatus(work.Key, DecisionStatus.Blocked, "Permanent failure or attempts exhausted", outcome); }
    }
    partial void StopExecution()
    {
        if (Executor is ILeasedDesiredExecutor leases) leases.ReleaseAllDesired();
        if (executionSession is { } session) session.Active = false;
        pendingWork.Clear(); pendingByKey.Clear();
        foreach (var flight in flights.Values) RequestCancellation(flight);
        executionStates.Clear(); SignalWork();
    }
    private void ScheduleRetry(ResourceKey key, ExecutionState state)
    {
        var ticks = Math.Min(executionLimits.RetryCap.Ticks, executionLimits.RetryBase.Ticks * Math.Pow(2, Math.Max(0, state.Attempts - 1)));
        state.RetryAt = clock.MonotonicNow + TimeSpan.FromTicks((long)ticks);
        SetDeadline(("retry", key), state.RetryAt.Value, () => Recompute(key));
    }
    private void ResetBlockedExecutions(string? capabilityId)
    {
        foreach (var pair in executionStates)
            if (capabilityId is null || pair.Key.CapabilityId == capabilityId) { pair.Value.Blocked = false; pair.Value.Attempts = 0; }
    }
    public bool ReportExecutionOutcome(ResourceKey key, long generation, Outcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status is OutcomeStatus.Pending or OutcomeStatus.Accepted) throw new ArgumentException("Final receipt required.");
        lock (gate)
        {
            if (!flights.TryGetValue(key, out var flight) || flight.Work.Generation != generation || flight.ReceiptId != outcome.CorrelationId) return false;
            return flight.FinalReceipt.TrySetResult(outcome);
        }
    }
    public Task<Outcome> WaitForExecutionReceiptAsync(ResourceKey key, CancellationToken cancellationToken = default)
    {
        lock (gate) return flights[key].FirstReceipt.Task.WaitAsync(cancellationToken);
    }
    public Outcome SubmitManualCommand(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (gate)
        {
            RequireAutomation();
            if (acceptedCommands.Contains(command.CommandId)) return new(OutcomeStatus.Rejected, "DuplicateCommandId", command.CommandId);
            if (executionSession is not { Active: true } || command.Deadline <= clock.UtcNow ||
                !capabilities.TryGetValue(command.Key.CapabilityId, out var binding) || !binding.Descriptor.Available || binding.Descriptor.Scope != command.Key.Scope.Kind ||
                !binding.Descriptor.Command || !binding.Accepts(command.Arguments) ||
                !Authorize(command.Principal, command.Key, CapabilityOperation.ExecuteCommand).Allowed)
                return new(OutcomeStatus.Rejected, "Manual command unavailable, unauthorized or expired", command.CommandId);
            if (pendingByKey.ContainsKey(command.Key)) return new(OutcomeStatus.Rejected, "Resource already has pending work", command.CommandId);
            TakeOverride(command.Key, command.Principal, "Explicit manual command");
            var action = RuleAction.Command(command.Key, command.Arguments, command.IsIdempotent);
            var work = new Work(command.Key, decisions[command.Key].Generation, action, command.Principal, command.CommandId, binding.Descriptor, [], true, command.Deadline,
                RequiredResources: ExpandResources([command.Key]).ToImmutableArray());
            return Enqueue(work) ? new(OutcomeStatus.Accepted, "Queued under manual override", command.CommandId) : new(OutcomeStatus.Rejected, "Capacity exceeded", command.CommandId);
        }
    }
}
