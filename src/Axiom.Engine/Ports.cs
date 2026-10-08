using Axiom.Domain;

namespace Axiom.Engine;

public interface IEngineClock
{
    DateTimeOffset UtcNow { get; }
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
