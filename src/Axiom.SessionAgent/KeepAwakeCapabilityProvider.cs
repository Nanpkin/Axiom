using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal interface ISessionDesiredCapabilityProvider
{
    IDisposable AcquireDesired(TaskInvocation invocation, CancellationToken token);
}

// Reports ownership of Axiom's request, not the machine's global sleep eligibility.
internal sealed class KeepAwakeCapabilityProvider(Func<string, IDisposable>? acquire = null) : ISessionCapabilityProvider, ISessionDesiredCapabilityProvider
{
    private readonly object gate = new();
    private readonly Dictionary<string, (IDisposable Request, int Count)> requests = [];
    private readonly Dictionary<long, (ProviderResource[] Resources, Action<ProviderUpdate> Publish)> subscribers = [];
    private long nextId;
    public IReadOnlyCollection<string> Capabilities => [KeepAwakeCapabilities.Hold];
    private ProviderSample Sample(ProviderResource resource) => new(resource, StateQuality.Known, "axiom.bool", 1,
        Boolean: requests.ContainsKey(resource.ResourceId));
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Any(r => r.CapabilityId != KeepAwakeCapabilities.Hold || r.ResourceId is not ("system" or "display")))
            throw new ArgumentException("保持唤醒检测资源无效。");
        long id;
        lock (gate)
        {
            token.ThrowIfCancellationRequested(); id = ++nextId;
            subscribers.Add(id, (resources, publish));
            publish(new(true, resources.Select(Sample).ToArray()));
        }
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        finally { lock (gate) subscribers.Remove(id); }
    }
    public IDisposable AcquireDesired(TaskInvocation invocation, CancellationToken token)
    {
        var target = KeepAwakeCapabilities.Validate(invocation);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (requests.TryGetValue(target, out var held)) requests[target] = (held.Request, checked(held.Count + 1));
            else
            {
                var request = (acquire ?? WindowsPowerRequest.Acquire)(target);
                try { token.ThrowIfCancellationRequested(); }
                catch { request.Dispose(); throw; }
                requests.Add(target, (request, 1)); Publish(target);
            }
            return new Lease(this, target);
        }
    }
    private void Release(string target)
    {
        lock (gate)
        {
            if (!requests.TryGetValue(target, out var held)) return;
            if (held.Count > 1) { requests[target] = (held.Request, held.Count - 1); return; }
            // The last lease must close the native handle before publishing false.
            try { held.Request.Dispose(); }
            finally { requests.Remove(target); Publish(target); }
        }
    }
    private void Publish(string target)
    {
        var resource = new ProviderResource(KeepAwakeCapabilities.Hold, target);
        foreach (var subscriber in subscribers.Values)
            if (subscriber.Resources.Contains(resource)) subscriber.Publish(new(false, [Sample(resource)]));
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) =>
        Task.FromResult((OutcomeStatus.Rejected, "保持唤醒必须使用条件租约，不能作为一次性命令执行。"));
    private sealed class Lease(KeepAwakeCapabilityProvider owner, string target) : IDisposable
    {
        private KeepAwakeCapabilityProvider? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release(target);
    }
}
