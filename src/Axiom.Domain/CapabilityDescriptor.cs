namespace Axiom.Domain;

public enum ExecutionLocation { Service, SessionAgent }
public enum EventFidelity { LatestValue, EdgeSensitive }
public enum RetryCategory { Never, IdempotentTransientOnly }
public enum ConfirmationSource { ReceiptOnly, Observation }
public enum CostClass { Low, Moderate, High }
public enum ConcurrencyScope { Resource, Capability }

public sealed record CapabilityDescriptor
{
    public string Id { get; }
    public ValueSchema Parameters { get; }
    public ValueSchema Result { get; }
    public ScopeKind Scope { get; }
    public bool Observe { get; }
    public bool Command { get; }
    public bool DesiredState { get; }
    public ExecutionLocation Location { get; }
    public string RequiredPermission { get; }
    public bool Available { get; }
    public string AvailabilityReason { get; }
    public bool AbsoluteIdempotentSetting { get; }
    public TimeSpan Timeout { get; }
    public bool Cancellable { get; }
    public ConcurrencyScope Concurrency { get; }
    public RetryCategory Retry { get; }
    public ConfirmationSource Confirmation { get; }
    public EventFidelity Fidelity { get; }
    public TimeSpan MaximumRefreshDelay { get; }
    public TimeSpan MinimumApplyInterval { get; }
    public CostClass Cost { get; }
    public bool SupportsLightweight { get; }
    public string ActivationCondition { get; }
    public string AuthorizationPolicy { get; }
    public System.Collections.Immutable.ImmutableArray<ResourceKey> Dependencies { get; }
    public bool SharedByReferenceCount => true;
    public bool RequiresPolling => false;

    public CapabilityDescriptor(string id, ValueSchema parameters, ValueSchema result, ScopeKind scope,
        bool observe, bool command, bool desiredState, ExecutionLocation location, string requiredPermission,
        bool available, string availabilityReason, bool absoluteIdempotentSetting, TimeSpan timeout,
        bool cancellable, ConcurrencyScope concurrency, RetryCategory retry, ConfirmationSource confirmation,
        EventFidelity fidelity, TimeSpan maximumRefreshDelay, TimeSpan minimumApplyInterval, CostClass cost,
        bool supportsLightweight, string activationCondition, string authorizationPolicy, IEnumerable<ResourceKey> dependencies)
    {
        Id = Contract.Text(id, nameof(id));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Scope = Contract.EnumValue(scope);
        if (!observe && !command && !desiredState) throw new ArgumentException("Capability has no supported operation.");
        if (desiredState && !absoluteIdempotentSetting) throw new ArgumentException("Settings must use absolute idempotent targets.");
        if (timeout <= TimeSpan.Zero || maximumRefreshDelay < TimeSpan.Zero || minimumApplyInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!observe && confirmation == ConfirmationSource.Observation) throw new ArgumentException("Observation confirmation needs observation support.");
        (Observe, Command, DesiredState) = (observe, command, desiredState);
        Location = Contract.EnumValue(location);
        RequiredPermission = Contract.Text(requiredPermission, nameof(requiredPermission));
        Available = available;
        AvailabilityReason = Contract.Text(availabilityReason, nameof(availabilityReason));
        (AbsoluteIdempotentSetting, Timeout, Cancellable) = (absoluteIdempotentSetting, timeout, cancellable);
        Concurrency = Contract.EnumValue(concurrency);
        Retry = Contract.EnumValue(retry);
        Confirmation = Contract.EnumValue(confirmation);
        Fidelity = Contract.EnumValue(fidelity);
        (MaximumRefreshDelay, MinimumApplyInterval) = (maximumRefreshDelay, minimumApplyInterval);
        Cost = Contract.EnumValue(cost);
        SupportsLightweight = supportsLightweight;
        ActivationCondition = Contract.Text(activationCondition, nameof(activationCondition));
        AuthorizationPolicy = Contract.Text(authorizationPolicy, nameof(authorizationPolicy));
        ArgumentNullException.ThrowIfNull(dependencies);
        Dependencies = System.Collections.Immutable.ImmutableArray.CreateRange(dependencies.Select(x => x ?? throw new ArgumentException("Null dependency.")));
    }
}
