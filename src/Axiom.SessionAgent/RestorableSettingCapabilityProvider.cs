using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal interface ISessionRestorableCapabilityProvider
{
    Task<RestorableSettingCapabilityProvider.Lease> AcquireAsync(TaskInvocation invocation, CancellationToken token);
}

// Ownership observation: idle is Known empty text; only a real native-confirmed apply echoes its target.
internal class RestorableSettingCapabilityProvider(string capabilityId,
    Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<IRestorableSettingSession>> create,
    Action<TaskInvocation> validate, Func<TaskInvocation, string> resourceId, TimeSpan? cleanupTimeout = null) : ISessionCapabilityProvider, ISessionRestorableCapabilityProvider
{
    private readonly object gate = new();
    private readonly Dictionary<string, Lease?> leases = [];
    private readonly Dictionary<long, (ProviderResource[] Resources, Action<ProviderUpdate> Publish)> subscribers = [];
    private long nextId;
    private string RegisteredId => capabilityId;
    private void ValidateInvocation(TaskInvocation invocation) => validate(invocation);
    private string ResolveResource(TaskInvocation invocation) => resourceId(invocation);
    public IReadOnlyCollection<string> Capabilities => [capabilityId];
    private ProviderSample Sample(ProviderResource resource)
    {
        var lease = leases.GetValueOrDefault(resource.ResourceId);
        return lease?.Target is { } target
            ? new(resource, StateQuality.Known, RestorableSettingInvocations.Schema.Id, 1, Text: target, Reason: "Axiom本轮设置已读回确认；不是全系统持续观测。")
            : new(resource, StateQuality.Known, RestorableSettingInvocations.Schema.Id, 1, Text: "", Reason: "Axiom没有已确认的本轮设置租约；原设置基线尚未捕获。");
    }
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 128 || resources.Any(r => r.CapabilityId != capabilityId || string.IsNullOrWhiteSpace(r.ResourceId)))
            throw new ArgumentException("设置恢复观察资源无效。");
        long id;
        lock (gate)
        {
            token.ThrowIfCancellationRequested(); id = ++nextId;
            subscribers.Add(id, (resources, publish)); publish(new(true, resources.Select(Sample).ToArray()));
        }
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        finally { lock (gate) subscribers.Remove(id); }
    }
    public async Task<Lease> AcquireAsync(TaskInvocation invocation, CancellationToken token)
    {
        if (invocation.CapabilityId != capabilityId) throw new ArgumentException("设置恢复能力不匹配。");
        validate(invocation); var id = resourceId(invocation);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (leases.ContainsKey(id) || leases.Count >= 128) throw new InvalidOperationException("前一次设置租约仍在应用或释放，本次不排队、不另抓基线。");
            leases.Add(id, null);
        }
        IRestorableSettingSession? session = null;
        try
        {
            session = await create(invocation.Parameters, token);
            var lease = new Lease(this, id, session);
            lock (gate) leases[id] = lease;
            session = null;
            lease.Initial = await lease.UpdateAsync(invocation, token);
            return lease;
        }
        catch
        {
            Lease? existing; lock (gate) existing = leases.GetValueOrDefault(id);
            if (existing is not null) await existing.DisposeAsync();
            else
            {
                if (session is not null) await session.DisposeAsync();
                lock (gate) { leases.Remove(id); Publish(id); }
            }
            throw;
        }
    }
    private void Publish(string id)
    {
        var resource = new ProviderResource(capabilityId, id);
        foreach (var subscriber in subscribers.Values)
            if (subscriber.Resources.Contains(resource)) subscriber.Publish(new(false, [Sample(resource)]));
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) =>
        Task.FromResult((OutcomeStatus.Rejected, "条件恢复设置必须使用持续条件租约，不能作为一次性命令。"));
    internal sealed class Lease(RestorableSettingCapabilityProvider owner, string id, IRestorableSettingSession session) : IAsyncDisposable
    {
        internal TimeSpan CleanupTimeout => owner.CleanupTimeout;
        private readonly SemaphoreSlim serial = new(1, 1);
        internal RestorableSettingResult Initial { get; set; } = new(false, "尚未确认应用。", false);
        internal string? Target;
        private bool released;
        public async Task<RestorableSettingResult> UpdateAsync(TaskInvocation invocation, CancellationToken token)
        {
            owner.ValidateInvocation(invocation);
            if (invocation.CapabilityId != owner.RegisteredId || owner.ResolveResource(invocation) != id) throw new ArgumentException("租约不能转而控制新资源。");
            await serial.WaitAsync(token);
            try
            {
                if (released) return new(false, "设置租约已释放。", false);
                var receipt = await session.ApplyAsync(invocation.Parameters, token);
                lock (owner.gate) { Target = receipt.Success && receipt.Owned ? RestorableSettingInvocations.Encode(invocation) : null; owner.Publish(id); }
                return receipt;
            }
            finally { serial.Release(); }
        }
        public async Task<RestorableSettingResult> ReleaseAsync(CancellationToken token)
        {
            await serial.WaitAsync(token);
            try
            {
                if (released) return new(true, "设置租约已释放。", false);
                released = true;
                try { return await session.ReleaseAsync(token); }
                finally
                {
                    await session.DisposeAsync();
                    lock (owner.gate) { Target = null; owner.leases.Remove(id); owner.Publish(id); }
                }
            }
            finally { serial.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            await ReleaseAsync(cleanup.Token);
        }
    }
    private TimeSpan CleanupTimeout => cleanupTimeout ?? TimeSpan.FromSeconds(15);
}
