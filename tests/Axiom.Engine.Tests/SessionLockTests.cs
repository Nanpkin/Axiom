using System.Buffers.Binary;
using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

// Every invocation below uses a fake API. Never call WindowsSessionLock.LockAsync in automated validation.
public sealed class SessionLockTests
{
    private static TaskInvocation Invocation(string delay = "0") => new(SessionLockCapabilities.Lock, 1,
        new Dictionary<string, string> { ["delaySeconds"] = delay }.ToImmutableDictionary());

    [Fact]
    public void RealRegistryUsesOneCurrentSessionResourceAndEngineDelayWithTimeWindowSupport()
    {
        var action = new SessionLockTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(action.SupportsTimeWindow);
        Assert.Equal("current-session-lock", action.Compile(Invocation(), scope).Key.ResourceId);
        Assert.Equal(action.Compile(Invocation(), scope).Key, action.Compile(Invocation("60"), scope).Key);
        Assert.Equal(TimeSpan.FromSeconds(60), action.Timing(Invocation("60")).MinimumDuration);
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<SessionLockPlugin>());
        foreach (var detection in new[] { UsbDeviceCapabilities.Absent, WifiProfileCapabilities.Disconnected, HotkeyCapabilities.Pressed,
            IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due, OnceTimeCapabilities.Due })
            Assert.Contains(plugin.Combinations, c => c.DetectionId == detection && c.ActionId == SessionLockCapabilities.Lock);
        Assert.Equal(RetryCategory.Never, Assert.Single(plugin.Bindings).Descriptor.Retry);
        Assert.DoesNotContain(plugin.Combinations, c => c.DetectionId == SystemEventCapabilities.Event); // no lock/unlock feedback loop recipe
        Assert.Contains("关闭Axiom窗口后后台仍", action.Descriptor.Behavior);
        Assert.Contains("正常登录解锁", action.Descriptor.Behavior);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("0.5")]
    [InlineData(" 1")]
    public void InvalidDelayIsRejectedBeforeAnyLockRequest(string value) =>
        Assert.Throws<ArgumentException>(() => SessionLockCapabilities.Validate(Invocation(value)));

    [Theory]
    [InlineData("sessionId", "2")]
    [InlineData("unlock", "true")]
    [InlineData("retry", "3")]
    public void TargetSessionUnlockAndRetryParametersAreNotAccepted(string field, string value) =>
        Assert.Throws<ArgumentException>(() => SessionLockCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add(field, value) }));

    [Fact]
    public void AcceptedRequestIsSentOnceAndNeverClaimsLockWasConfirmedOrPollsForIt()
    {
        var api = new FakeSessionLockApi(); var result = SessionLockOperation.Apply(api, default);
        Assert.True(result.Success); Assert.Contains("Windows已接受", result.Reason); Assert.Contains("完成锁定未确认", result.Reason);
        Assert.Equal(1, api.StateReads); Assert.Equal(1, api.LockRequests); Assert.Equal(SessionLockState.Unlocked, api.State);
        Assert.Contains("不会自动解锁", result.Reason);
    }

    [Theory]
    [InlineData((int)SessionLockState.Locked, "已经锁定")]
    [InlineData((int)SessionLockState.NonInteractive, "不是活动交互会话")]
    [InlineData((int)SessionLockState.Unknown, "无法确认")]
    public void LockedDisconnectedOrUnknownSessionNeverReceivesALockRequest(int state, string reason)
    {
        var api = new FakeSessionLockApi { State = (SessionLockState)state }; var result = SessionLockOperation.Apply(api, default);
        Assert.False(result.Success); Assert.Contains(reason, result.Reason); Assert.Equal(1, api.StateReads); Assert.Equal(0, api.LockRequests);
    }

    [Theory]
    [InlineData(5, "权限不足")]
    [InlineData(0, "未接受")]
    [InlineData(1314, "未接受")]
    public void NativeRefusalAndMissingLastErrorAreFailuresWithoutRetry(int error, string reason)
    {
        var api = new FakeSessionLockApi { Accepted = false, Error = error }; var result = SessionLockOperation.Apply(api, default);
        Assert.False(result.Success); Assert.Contains(reason, result.Reason); Assert.Contains("不自动重试", result.Reason);
        Assert.Equal(1, api.StateReads); Assert.Equal(1, api.LockRequests);
    }

    [Fact]
    public void CancellationBeforeAndDuringStateReadPreventsRequestsAndAfterAcceptedRequestDoesNotUndoIt()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new FakeSessionLockApi();
        Assert.ThrowsAny<OperationCanceledException>(() => SessionLockOperation.Apply(untouched, before.Token));
        Assert.Equal(0, untouched.StateReads); Assert.Equal(0, untouched.LockRequests);
        using var reading = new CancellationTokenSource(); var canceled = new FakeSessionLockApi { AfterState = reading.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => SessionLockOperation.Apply(canceled, reading.Token)); Assert.Equal(0, canceled.LockRequests);
        using var after = new CancellationTokenSource(); var requested = new FakeSessionLockApi { AfterRequest = after.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => SessionLockOperation.Apply(requested, after.Token));
        Assert.Equal(1, requested.LockRequests); Assert.Equal(1, requested.StateReads);
    }

    [Theory]
    [InlineData(0, (int)SessionLockState.Locked)]
    [InlineData(1, (int)SessionLockState.Unlocked)]
    [InlineData(-1, (int)SessionLockState.Unknown)]
    [InlineData(2, (int)SessionLockState.Unknown)]
    public void Windows11InfoExHeaderReadsFlagsFromAlignedUnionOnly(int flags, int expected)
    {
        var data = Header(2, 0, flags);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 123456); // padding must not be mistaken for the SessionId
        Assert.Equal((SessionLockState)expected, SessionLockStateReader.Parse(data, 2));
    }

    [Fact]
    public void WrongSessionShortHeaderUnknownLevelAndNonActiveConnectionCannotBeTreatedAsUnlocked()
    {
        var valid = Header(2, 0, 1);
        Assert.Equal(SessionLockState.Unknown, SessionLockStateReader.Parse(valid, 3));
        Assert.Equal(SessionLockState.Unknown, SessionLockStateReader.Parse(valid.AsSpan(0, 19), 2));
        Assert.Equal(SessionLockState.Unknown, SessionLockStateReader.Parse(Header(0, 0, 1), 0));
        var wrongLevel = Header(2, 0, 1); BinaryPrimitives.WriteUInt32LittleEndian(wrongLevel, 2);
        Assert.Equal(SessionLockState.Unknown, SessionLockStateReader.Parse(wrongLevel, 2));
        foreach (var connection in new[] { 1, 4, 5, 7, 8, 9 })
            Assert.Equal(SessionLockState.NonInteractive, SessionLockStateReader.Parse(Header(2, connection, 1), 2));
    }

    [Fact(Timeout = 10000)]
    public async Task CanceledNativeRequestKeepsAdmissionAndDrainUntilReturnWithoutSendingAnotherRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
        var commands = new BoundedNativeCommand("锁屏", TimeSpan.FromSeconds(5));
        var api = new FakeSessionLockApi { AfterRequest = () => { entered.SetResult(); release.Wait(); } };
        var request = commands.RunAsync(token => SessionLockOperation.Apply(api, token), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.False(commands.DrainAsync().IsCompleted);
            var overlap = await commands.RunAsync(token => SessionLockOperation.Apply(api, token), default);
            Assert.False(overlap.Success); Assert.Contains("本次未执行", overlap.Reason); Assert.Equal(1, api.LockRequests);
        }
        finally { release.Set(); await commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(1, api.LockRequests); Assert.Equal(1, api.StateReads);
    }

    private static byte[] Header(int session, int connection, int flags)
    {
        var data = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), session); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), connection);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), flags); return data;
    }
    private sealed class FakeSessionLockApi : ISessionLockApi
    {
        internal SessionLockState State = SessionLockState.Unlocked;
        internal bool Accepted = true; internal int Error, StateReads, LockRequests;
        internal Action? AfterState, AfterRequest;
        public SessionLockState GetCurrentState() { StateReads++; AfterState?.Invoke(); return State; }
        public (bool Accepted, int Error) RequestLock() { LockRequests++; AfterRequest?.Invoke(); return (Accepted, Error); }
    }
}
