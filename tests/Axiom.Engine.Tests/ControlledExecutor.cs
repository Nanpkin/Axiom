using Axiom.Domain;

namespace Axiom.Engine.Tests;

internal sealed class ControlledExecutor : ICapabilityExecutor
{
    internal sealed class Call(Command? command, DesiredState? desired, CancellationToken token, int ordinal)
    {
        internal Command? Command = command;
        internal DesiredState? Desired = desired;
        internal CancellationToken Token = token;
        internal Guid Id = command?.CommandId ?? new Guid(ordinal, 1, 0, new byte[8]);
        internal TaskCompletionSource<Outcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(OutcomeStatus status = OutcomeStatus.Succeeded, FailureKind failure = FailureKind.None) =>
            Completion.TrySetResult(new(status, "controlled fake", Id, failure));
    }
    private readonly object gate = new();
    private readonly Queue<Call> calls = new();
    private readonly SemaphoreSlim available = new(0);
    internal int Count { get; private set; }
    public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken cancellationToken) => Start(command, null, cancellationToken);
    public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken cancellationToken) => Start(null, desired, cancellationToken);
    private ValueTask<Outcome> Start(Command? command, DesiredState? desired, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var call = new Call(command, desired, cancellationToken, ++Count);
            calls.Enqueue(call); available.Release();
            return new(call.Completion.Task);
        }
    }
    internal async Task<Call> NextAsync()
    {
        await available.WaitAsync();
        lock (gate) return calls.Dequeue();
    }
}
