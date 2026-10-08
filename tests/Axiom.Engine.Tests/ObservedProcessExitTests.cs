using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class ObservedProcessExitTests
{
    [Fact(Timeout = 10000)]
    public async Task ExactHandleSignalPublishesOneUnknownOriginExitAndCancellationReleasesTheRest()
    {
        var result = new TaskCompletionSource<(DateTimeOffset At, string Path)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var at = DateTimeOffset.UtcNow; var one = new ManualResetEvent(false); var two = new ManualResetEvent(false);
        var tracker = new ObservedProcessExitTracker((time, path) => result.TrySetResult((time, path)), default);
        tracker.Track(42, 100, one, () => at, "Example.exe");
        tracker.Track(42, 200, two, () => throw new InvalidOperationException("new instance has not exited"), "Other.exe");
        one.Set(); Assert.Equal((at, "Example.exe"), await result.Task);
        await tracker.DisposeAsync(); Assert.Equal(0, tracker.Count);
        Assert.True(one.SafeWaitHandle.IsClosed, "Signaled process handle must be closed before disposal completes.");
        Assert.True(two.SafeWaitHandle.IsClosed, "Cancelled process handle must be closed before disposal completes.");
        Assert.False(tracker.Failure.IsCompleted);
    }
    [Fact(Timeout = 10000)]
    public async Task CapacityAndDuplicateRejectionDisposeHandlesAndDoNotLeakWaitingTasks()
    {
        var tracker = new ObservedProcessExitTracker((_, _) => throw new InvalidOperationException(), default);
        for (var i = 0; i < 128; i++) tracker.Track(i, 1, new ManualResetEvent(false), () => DateTimeOffset.UtcNow, "Example.exe");
        var duplicate = new ManualResetEvent(false); tracker.Track(0, 1, duplicate, () => DateTimeOffset.UtcNow, "Example.exe"); Assert.True(duplicate.SafeWaitHandle.IsClosed);
        var overflow = new ManualResetEvent(false); Assert.Throws<IOException>(() => tracker.Track(129, 1, overflow, () => DateTimeOffset.UtcNow, "Example.exe")); Assert.True(overflow.SafeWaitHandle.IsClosed);
        Assert.Equal(128, tracker.Count); await tracker.DisposeAsync(); Assert.Equal(0, tracker.Count);
    }
    [Fact(Timeout = 10000)]
    public async Task FailedExitReadIsReportedInsteadOfInventingAnExit()
    {
        var handle = new ManualResetEvent(false); var published = false;
        await using var tracker = new ObservedProcessExitTracker((_, _) => published = true, default);
        tracker.Track(1, 1, handle, () => throw new IOException("synthetic missing exit time"), "Example.exe"); handle.Set();
        await Assert.ThrowsAsync<IOException>(() => tracker.Failure); Assert.False(published);
    }
}
