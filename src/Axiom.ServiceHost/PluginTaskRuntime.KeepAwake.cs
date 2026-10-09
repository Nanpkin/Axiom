using System.Collections.Immutable;
using System.IO.Pipes;
using Axiom.Contracts;
using Axiom.Domain;

namespace Axiom.ServiceHost;

internal sealed partial class PluginTaskRuntime
{
    private readonly object powerGate = new();
    private readonly Dictionary<ResourceKey, PowerAuthorization> powerAuthorizations = [];
    private readonly HashSet<PowerLease> powerLeases = [];
    private sealed class PowerAuthorization(long generation, bool authorized)
    {
        public long Generation = generation;
        public bool Authorized = authorized;
        public long? FailedGeneration;
        public PowerLease? Lease;
    }
    private sealed class PowerLease(NamedPipeClientStream pipe)
    {
        public readonly NamedPipeClientStream Pipe = pipe;
        public readonly CancellationTokenSource Lifetime = new();
        public Task? Cancellation;
        public Task? Monitor;
        public readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Cancel() => Cancellation ??= Lifetime.CancelAsync();
    }
    public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized)
    {
        if (key.CapabilityId != KeepAwakeCapabilities.Hold) return;
        lock (powerGate)
        {
            if (!powerAuthorizations.TryGetValue(key, out var item)) powerAuthorizations.Add(key, item = new(generation, authorized));
            item.Generation = generation; item.Authorized = authorized;
            // An unchanged true target can retain its request as winning rules change.
            if (!authorized && item.Lease is { } lease) { item.Lease = null; lease.Cancel(); }
        }
    }
    public void ReleaseAllDesired()
    {
        lock (powerGate)
            foreach (var item in powerAuthorizations.Values)
            {
                item.Authorized = false;
                if (item.Lease is { } lease) { item.Lease = null; lease.Cancel(); }
            }
    }
    private async ValueTask<Outcome> ApplyKeepAwakeAsync(DesiredState desired, CancellationToken token)
    {
        var id = Guid.NewGuid();
        if (stopping || desired.Key.CapabilityId != KeepAwakeCapabilities.Hold || desired.Key.Scope != scope ||
            desired.Key.ResourceId is not ("system" or "display") || desired.Target.Boolean != true || desired.Target.Schema != new ValueSchema("axiom.bool", 1))
            return new(OutcomeStatus.Rejected, "保持唤醒目标或会话不匹配。", id);
        PowerLease lease;
        lock (powerGate)
        {
            if (!powerAuthorizations.TryGetValue(desired.Key, out var item) || !item.Authorized || item.Generation != desired.Generation)
                return new(OutcomeStatus.Rejected, "保持唤醒条件已结束。", id);
            if (item.FailedGeneration == desired.Generation)
                return new(OutcomeStatus.Failed, "保持唤醒连接已中断；等待条件重新成立或暂停后启用，不会自动重新申请。", id, FailureKind.Permanent);
            if (item.Lease is not null) return new(OutcomeStatus.Succeeded, "Axiom 已持有此保持唤醒请求。", id);
            item.Lease = lease = new(Client());
            powerLeases.Add(lease);
        }
        var retained = false;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Lifetime.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(4));
            await lease.Pipe.ConnectForUserAsync(2000, handshake.Token);
            var requestId = id.ToString("N");
            var invocation = new TaskInvocation(KeepAwakeCapabilities.Hold, 1,
                ImmutableDictionary<string, string>.Empty.Add("target", desired.Key.ResourceId));
            await ProviderProtocol.WriteAsync(lease.Pipe, new(1, "lease", requestId, DateTimeOffset.UtcNow.AddSeconds(4), Invocation: invocation), handshake.Token);
            var response = await ProviderProtocol.ReadAsync(lease.Pipe, handshake.Token);
            if (response.Version != 1 || response.Type != "result" || response.RequestId != requestId || response.CommandId is not null ||
                response.Status is not (OutcomeStatus.Succeeded or OutcomeStatus.Failed or OutcomeStatus.Rejected) || string.IsNullOrWhiteSpace(response.Reason))
                throw new InvalidDataException("保持唤醒回执无效。");
            if (response.Status != OutcomeStatus.Succeeded)
            {
                BlockFailedPowerApply(desired.Key, lease, desired.Generation);
                return new(response.Status!.Value, response.Reason, id, FailureKind.Permanent);
            }
            lock (powerGate)
            {
                token.ThrowIfCancellationRequested();
                if (lease.Lifetime.IsCancellationRequested || !powerAuthorizations.TryGetValue(desired.Key, out var current) ||
                    !current.Authorized || !ReferenceEquals(current.Lease, lease)) throw new OperationCanceledException("保持唤醒条件已结束。");
                retained = true;
            }
            Record("已保持唤醒", desired.Key.ResourceId == "display" ? "条件成立，已申请保持系统和屏幕运行。" : "条件成立，已申请保持系统运行；屏幕仍可关闭。", ActivityLevel.Success);
            // Starting an async reader can synchronously complete; never start it under powerGate.
            lease.Monitor = MonitorPowerLeaseAsync(desired.Key, lease);
            return new(OutcomeStatus.Succeeded, response.Reason, id);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            BlockFailedPowerApply(desired.Key, lease, desired.Generation);
            var message = error is OperationCanceledException ? "保持唤醒请求已取消或通信中断，连接关闭后释放请求。" : error.Message;
            Record("保持唤醒未完成", message, ActivityLevel.Warning);
            return new(OutcomeStatus.Failed, message, id, FailureKind.Permanent);
        }
        finally { if (!retained) await DisposePowerLeaseAsync(desired.Key, lease); }
    }
    private async Task MonitorPowerLeaseAsync(ResourceKey key, PowerLease lease)
    {
        try
        {
            await ProviderProtocol.ReadAsync(lease.Pipe, lease.Lifetime.Token);
            BlockLostPowerLease(key, lease);
            Record("保持唤醒连接中断", "会话程序结束了保持唤醒连接；等待新的条件或暂停后启用。", ActivityLevel.Warning);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            if (!lease.Lifetime.IsCancellationRequested)
            {
                BlockLostPowerLease(key, lease);
                Record("保持唤醒连接中断", "会话程序连接不可用；等待新的条件或暂停后启用，不会自动重新申请。", ActivityLevel.Warning);
            }
        }
        finally
        {
            await DisposePowerLeaseAsync(key, lease);
        }
    }
    private void BlockLostPowerLease(ResourceKey key, PowerLease lease)
    {
        lock (powerGate)
            if (powerAuthorizations.TryGetValue(key, out var item) && ReferenceEquals(item.Lease, lease))
                item.FailedGeneration = item.Generation;
    }
    private void BlockFailedPowerApply(ResourceKey key, PowerLease lease, long generation)
    {
        lock (powerGate)
            if (powerAuthorizations.TryGetValue(key, out var item) && item.Authorized && item.Generation == generation &&
                ReferenceEquals(item.Lease, lease) && !lease.Lifetime.IsCancellationRequested)
                item.FailedGeneration = generation;
    }
    private async Task DisposePowerLeaseAsync(ResourceKey key, PowerLease lease)
    {
        Task? cancellation;
        lock (powerGate)
        {
            if (powerAuthorizations.TryGetValue(key, out var item) && ReferenceEquals(item.Lease, lease)) item.Lease = null;
            cancellation = lease.Cancellation;
        }
        try
        {
            lease.Pipe.Dispose();
            if (cancellation is not null) await cancellation;
            lease.Lifetime.Dispose();
        }
        finally
        {
            lock (powerGate) powerLeases.Remove(lease);
            lease.Released.TrySetResult();
        }
    }
    private Task DrainPowerLeasesAsync(CancellationToken token)
    {
        ReleaseAllDesired();
        lock (powerGate) return Task.WhenAll(powerLeases.Select(lease => lease.Released.Task)).WaitAsync(token);
    }
}
