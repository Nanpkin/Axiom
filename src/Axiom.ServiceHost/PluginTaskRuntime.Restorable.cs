using System.IO.Pipes;
using Axiom.Contracts;
using Axiom.Domain;

namespace Axiom.ServiceHost;

internal sealed partial class PluginTaskRuntime
{
    public bool IsReleasedObservation(ResourceKey key, StateValue value) => key.CapabilityId == KeepAwakeCapabilities.Hold
        ? value.Boolean == false : RestorableSettingInvocations.IsRestorable(key.CapabilityId) && value.Schema == RestorableSettingInvocations.Schema && value.Text == "";
    private readonly object settingGate = new();
    private readonly Dictionary<ResourceKey, SettingAuthorization> settingAuthorizations = [];
    private readonly HashSet<SettingLease> settingLeases = [];
    private sealed class SettingAuthorization(long generation, bool authorized)
    {
        public long Generation = generation;
        public bool Authorized = authorized;
        public long? FailedGeneration;
        public SettingLease? Lease;
    }
    private sealed class SettingLease(NamedPipeClientStream pipe)
    {
        public readonly NamedPipeClientStream Pipe = pipe;
        public readonly SemaphoreSlim Serial = new(1, 1);
        public readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Lifetime = new();
        public TaskCompletionSource<ProviderMessage>? Reply;
        public Task? Monitor;
        public Task? Release;
        public long Sequence;
        public string? Target;
        public bool Connected;
        public string? CapturedSelection;
        public bool ReleaseConfirmed;
        public TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    }
    private void SetRestorableAuthorization(ResourceKey key, long generation, bool authorized)
    {
        SettingLease? release = null;
        lock (settingGate)
        {
            if (!settingAuthorizations.TryGetValue(key, out var item)) settingAuthorizations.Add(key, item = new(generation, authorized));
            item.Generation = generation; item.Authorized = authorized;
            if (!authorized && item.Lease is { } lease && MarkSettingRelease(lease)) release = lease;
        }
        if (release is not null) QueueSettingRelease(key, release);
    }
    private void ReleaseRestorableSettings()
    {
        var releases = new List<(ResourceKey Key, SettingLease Lease)>();
        lock (settingGate)
            foreach (var pair in settingAuthorizations)
            {
                pair.Value.Authorized = false;
                if (pair.Value.Lease is { } lease && MarkSettingRelease(lease)) releases.Add((pair.Key, lease));
            }
        foreach (var item in releases) QueueSettingRelease(item.Key, item.Lease);
    }
    // Keep Monitor scopes in synchronous methods. No transport, task scheduling or publication
    // runs while this state lock is held, including the cancellation/error paths.
    private SettingLease? PriorSettingLease(ResourceKey key, long generation, string? selection, out bool startRelease)
    {
        lock (settingGate)
        {
            startRelease = false;
            if (!settingAuthorizations.TryGetValue(key, out var held) || !held.Authorized || held.Generation != generation ||
                held.Lease is not { } old || old.Release is null && old.CapturedSelection == selection) return null;
            startRelease = MarkSettingRelease(old); return old;
        }
    }
    private (SettingLease? Lease, bool First, string? Error) ClaimSettingLease(ResourceKey key, long generation,
        string? selection, TimeSpan operationTimeout, NamedPipeClientStream candidate)
    {
        lock (settingGate)
        {
            if (!settingAuthorizations.TryGetValue(key, out var item) || !item.Authorized || item.Generation != generation)
                return (null, false, "设置条件已结束或代际已变化。");
            if (item.FailedGeneration == generation)
                return (null, false, "本轮设置租约未确认；等待新条件或暂停后启用，不自动重试。");
            var first = item.Lease is null;
            if (first) { item.Lease = new(candidate) { CapturedSelection = selection }; settingLeases.Add(item.Lease); }
            item.Lease!.OperationTimeout = operationTimeout;
            return (item.Lease, first, null);
        }
    }
    private int SettingAuthorizationState(ResourceKey key, SettingLease lease, long generation)
    {
        lock (settingGate) return !settingAuthorizations.TryGetValue(key, out var current) || !current.Authorized ||
            !ReferenceEquals(current.Lease, lease) ? 0 : current.Generation == generation ? 2 : 1;
    }
    private void FailSettingGeneration(ResourceKey key, long generation)
    { lock (settingGate) if (settingAuthorizations.TryGetValue(key, out var current) && current.Generation == generation) current.FailedGeneration = generation; }
    private async ValueTask<Outcome> ApplyRestorableAsync(DesiredState desired, CancellationToken token)
    {
        var id = Guid.NewGuid();
        if (wheelTakenOver && IsBrightness(desired.Key.CapabilityId)) return new(OutcomeStatus.Rejected, "亮度已明确由手动接管，未捕获新基线。", id);
        if (stopping || desired.Key.Scope != scope || desired.Target.Schema != RestorableSettingInvocations.Schema || desired.Target.Text is null ||
            !bindings.TryGetValue(desired.Key.CapabilityId, out var binding) || !binding.Descriptor.DesiredState)
            return new(OutcomeStatus.Rejected, "条件恢复目标、权限或会话不匹配。", id);
        var invocation = RestorableSettingInvocations.Decode(desired.Target.Text);
        if (invocation.CapabilityId != desired.Key.CapabilityId) return new(OutcomeStatus.Rejected, "设置租约能力与目标不匹配。", id);
        RestorableSettingInvocations.ValidateLease(invocation);
        var fading = invocation.CapabilityId is BuiltInBrightnessRestoreCapabilities.Set or HardwareBrightnessRestoreCapabilities.Set &&
            BrightnessFadeSettings.Parse(invocation.Parameters).Enabled;
        var operationTimeout = fading ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(15);
        var acquireTimeout = fading ? TimeSpan.FromSeconds(37) : TimeSpan.FromSeconds(12);
        // This currently has one real consumer: hardware brightness primary/all selects fixed handles.
        var selection = invocation.CapabilityId == HardwareBrightnessCapabilities.Set + ".restore" ? invocation.Parameters.GetValueOrDefault("target") : null;
        var prior = PriorSettingLease(desired.Key, desired.Generation, selection, out var startPriorRelease);
        if (prior is not null)
        {
            if (startPriorRelease) QueueSettingRelease(desired.Key, prior);
            try { await prior.Released.Task.WaitAsync(token); }
            catch (OperationCanceledException) { return new(OutcomeStatus.Failed, "原目标仍在释放，本次未捕获新范围；不自动重试。", id, FailureKind.Permanent); }
            if (!prior.ReleaseConfirmed)
            {
                FailSettingGeneration(desired.Key, desired.Generation);
                Record("新范围未执行", "原目标释放结果未确认；没有捕获或修改新范围，不自动重试。", ActivityLevel.Warning);
                return new(OutcomeStatus.Failed, "原目标释放结果未确认，新范围未执行。", id, FailureKind.Permanent);
            }
        }
        var candidate = Client();
        var claim = ClaimSettingLease(desired.Key, desired.Generation, selection, operationTimeout, candidate);
        if (!claim.First) candidate.Dispose();
        if (claim.Lease is not { } lease) return new(OutcomeStatus.Failed, claim.Error!, id, FailureKind.Permanent);
        var first = claim.First;
        var entered = false;
        try
        {
            await lease.Serial.WaitAsync(token); entered = true;
            // Once an initial frame has been sent, drain its reply even if the condition ends.
            // Canceling a partial pipe read loses framing and prevents a confirmed release.
            using var deadline = first ? CancellationTokenSource.CreateLinkedTokenSource(lease.Lifetime.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(token, lease.Lifetime.Token);
            deadline.CancelAfter(first ? acquireTimeout : operationTimeout);
            ProviderMessage receipt;
            if (first)
            {
                token.ThrowIfCancellationRequested();
                await lease.Pipe.ConnectForUserAsync(2000, deadline.Token);
                token.ThrowIfCancellationRequested();
                await ProviderProtocol.WriteAsync(lease.Pipe, new(1, "lease-setting", id.ToString("N"), DateTimeOffset.UtcNow.Add(acquireTimeout), Invocation: invocation), deadline.Token);
                receipt = await ProviderProtocol.ReadAsync(lease.Pipe, deadline.Token);
                ValidateSettingReceipt(receipt, id.ToString("N"), 0);
                lease.Connected = true;
                lease.Target = id.ToString("N"); // Stable connection request id, separate from the current Desired target.
                lease.Monitor = MonitorSettingLeaseAsync(desired.Key, lease);
            }
            else
            {
                if (!lease.Connected || lease.Release is not null) throw new IOException("原设置租约正在释放或尚未确认。");
                receipt = await SettingExchangeAsync(lease, "lease-setting-update", invocation, deadline.Token);
            }
            if (receipt.Status != OutcomeStatus.Succeeded)
            {
                BlockSettingLease(desired.Key, lease, desired.Generation);
                StartSettingRelease(desired.Key, lease);
                Record("条件设置未完成", receipt.Reason!, ActivityLevel.Warning);
                return new(receipt.Status!.Value, receipt.Reason!, id, FailureKind.Permanent);
            }
            var authorization = SettingAuthorizationState(desired.Key, lease, desired.Generation);
            if (authorization == 0) throw new OperationCanceledException("设置条件已结束。");
            if (authorization == 1) return new(OutcomeStatus.Rejected, "设置代际已变化；原租约供当前代际更新，不将旧回执报告为当前结果。", id);
            Record("条件设置已确认", receipt.Reason!, ActivityLevel.Success);
            return new(OutcomeStatus.Succeeded, receipt.Reason!, id);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            BlockSettingLease(desired.Key, lease, desired.Generation);
            StartSettingRelease(desired.Key, lease);
            var message = error is OperationCanceledException ? "条件设置已取消或通信超时；可能已修改，释放结果另行确认，不自动重试。" : error.Message;
            Record("条件设置未确认", message, ActivityLevel.Warning);
            return new(OutcomeStatus.Failed, message, id, FailureKind.Permanent);
        }
        finally { if (entered) lease.Serial.Release(); }
    }
    private static void ValidateSettingReceipt(ProviderMessage receipt, string id, long sequence)
    {
        if (receipt.Version != 1 || receipt.Type != "lease-setting-result" || receipt.RequestId != id || receipt.Sequence != sequence ||
            receipt.CommandId is not null || receipt.Status is not (OutcomeStatus.Succeeded or OutcomeStatus.Failed or OutcomeStatus.Rejected) || string.IsNullOrWhiteSpace(receipt.Reason))
            throw new InvalidDataException("设置租约回执缺失或代际无效。");
    }
    private async Task<ProviderMessage> SettingExchangeAsync(SettingLease lease, string type, TaskInvocation? invocation, CancellationToken token)
    {
        var (reply, sequence) = BeginSettingReply(lease);
        await ProviderProtocol.WriteAsync(lease.Pipe, new(1, type, lease.Target!, DateTimeOffset.UtcNow.Add(lease.OperationTimeout), Invocation: invocation, Sequence: sequence), token);
        return await reply.Task.WaitAsync(token);
    }
    private (TaskCompletionSource<ProviderMessage> Reply, long Sequence) BeginSettingReply(SettingLease lease)
    {
        lock (settingGate)
        {
            if (lease.Reply is not null) throw new IOException("前一次设置请求尚未返回，本次不排队。");
            var reply = lease.Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return (reply, ++lease.Sequence);
        }
    }
    private TaskCompletionSource<ProviderMessage> AcceptSettingReply(SettingLease lease, ProviderMessage receipt)
    {
        lock (settingGate)
        {
            ValidateSettingReceipt(receipt, lease.Target!, lease.Sequence);
            var reply = lease.Reply ?? throw new InvalidDataException("设置租约返回未请求的回执。");
            lease.Reply = null; return reply;
        }
    }
    private TaskCompletionSource<ProviderMessage>? TakeFailedSettingReply(SettingLease lease)
    {
        lock (settingGate) { var reply = lease.Reply; lease.Reply = null; return reply; }
    }
    private Task<ProviderMessage>? PendingSettingReply(SettingLease lease)
    { lock (settingGate) return lease.Reply?.Task; }
    private void ForgetSettingLease(ResourceKey key, SettingLease lease)
    {
        lock (settingGate)
        {
            // A failed restoration is not a new baseline. Keep a completed failure marker so
            // subsequent conditions cannot capture the partially dimmed value as the original.
            if (lease.ReleaseConfirmed && settingAuthorizations.TryGetValue(key, out var current) && ReferenceEquals(current.Lease, lease)) current.Lease = null;
            settingLeases.Remove(lease);
        }
    }
    private void RebaseManuallyChosenBrightness()
    {
        lock (settingGate)
            foreach (var pair in settingAuthorizations)
                if (IsBrightness(pair.Key.CapabilityId) && pair.Value.Lease is { ReleaseConfirmed: false } failed && failed.Released.Task.IsCompleted)
                    pair.Value.Lease = null;
    }
    private async Task MonitorSettingLeaseAsync(ResourceKey key, SettingLease lease)
    {
        try
        {
            while (!lease.Lifetime.IsCancellationRequested)
            {
                var receipt = await ProviderProtocol.ReadAsync(lease.Pipe, lease.Lifetime.Token);
                var reply = AcceptSettingReply(lease, receipt);
                reply.TrySetResult(receipt);
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            TakeFailedSettingReply(lease)?.TrySetException(new IOException("设置租约连接中断，释放结果尚未确认。"));
            if (lease.Release is null)
            {
                BlockSettingLease(key, lease, null);
                Record("条件设置连接中断", "会话端会尝试按本轮最后确认值释放；未收到恢复回执，不假称已恢复。", ActivityLevel.Warning);
                StartSettingRelease(key, lease);
            }
        }
    }
    private void BlockSettingLease(ResourceKey key, SettingLease lease, long? generation)
    {
        lock (settingGate)
            if (settingAuthorizations.TryGetValue(key, out var item) && ReferenceEquals(item.Lease, lease))
            { item.FailedGeneration = generation ?? item.Generation; }
    }
    private void StartSettingRelease(ResourceKey key, SettingLease lease)
    {
        bool start; lock (settingGate) start = MarkSettingRelease(lease);
        if (start) QueueSettingRelease(key, lease);
    }
    // Called only under settingGate; reserve the release before any asynchronous work starts.
    private static bool MarkSettingRelease(SettingLease lease)
    {
        if (lease.Release is not null) return false;
        lease.Release = lease.Released.Task; return true;
    }
    private void QueueSettingRelease(ResourceKey key, SettingLease lease) => _ = Task.Run(() => ReleaseSettingLeaseAsync(key, lease));
    private async Task ReleaseSettingLeaseAsync(ResourceKey key, SettingLease lease)
    {
        using var cleanup = new CancellationTokenSource(lease.OperationTimeout);
        var entered = false;
        try
        {
            await lease.Serial.WaitAsync(cleanup.Token); entered = true;
            var pending = PendingSettingReply(lease);
            if (pending is not null) await pending.WaitAsync(cleanup.Token);
            if (!lease.Connected) throw new IOException("尚未收到初始租约回执；关闭连接后的释放结果未确认。");
            var receipt = await SettingExchangeAsync(lease, "lease-setting-release", null, cleanup.Token);
            lease.ReleaseConfirmed = receipt.Status == OutcomeStatus.Succeeded;
            Record(receipt.Status == OutcomeStatus.Succeeded ? "条件设置已释放" : "条件设置释放未确认", receipt.Reason!,
                receipt.Status == OutcomeStatus.Succeeded ? ActivityLevel.Success : ActivityLevel.Warning);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { Record("条件设置释放未确认", "没有收到有效释放回执；会话端仍保留原生准入直到真实工作及清理结束。可能未恢复，不重试。", ActivityLevel.Warning); }
        finally
        {
            lease.Lifetime.Cancel(); lease.Pipe.Dispose();
            if (entered) lease.Serial.Release();
            if (lease.Monitor is { } monitor) await monitor;
            ForgetSettingLease(key, lease);
            lease.Lifetime.Dispose(); lease.Released.TrySetResult();
        }
    }
    private Task DrainRestorableSettingsAsync(CancellationToken token)
    {
        ReleaseRestorableSettings();
        return Task.WhenAll(SettingReleaseTasks()).WaitAsync(token);
    }
    private Task[] SettingReleaseTasks()
    { lock (settingGate) return settingLeases.Select(l => l.Released.Task).ToArray(); }
}
