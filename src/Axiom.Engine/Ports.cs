using Axiom.Domain;

namespace Axiom.Engine;

public interface IEngineClock
{
    DateTimeOffset UtcNow { get; }
    TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    TimeSpan MonotonicNow { get; }
}

// Absolute monotonic deadline; disposing the lease cancels only this scheduled item.
// An implementation must wait only for the nearest deadline, with no idle tick.
public interface IDeadlineScheduler
{
    IDisposable Schedule(TimeSpan deadline, Action callback);
}

// Requests must already be authorized by the future application pipeline.
// These methods are intentionally distinct: Command is never a reconciliation target.
public interface ICapabilityExecutor
{
    ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken cancellationToken);
    ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken cancellationToken);
}

// Optional, narrow lifecycle for executors that own removable requests (for example power requests).
// Called under Engine's gate: implementations must only update memory and signal asynchronously.
// A cancelled Apply must close its owned request lease before returning; its latest ownership
// observation remains usable when a replacement generation reconciles.
public interface ILeasedDesiredExecutor
{
    void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized);
    void ReleaseAllDesired();
}

public sealed record AuthorizationDecision(bool Allowed, string Reason);

// Fast, deterministic policy snapshot; must not perform I/O or re-enter Engine.
public interface IAuthorizationPolicy
{
    AuthorizationDecision Authorize(string principal, ResourceKey resource, CapabilityOperation operation);
}

public interface ICommandIdSource
{
    Guid NextId();
}
