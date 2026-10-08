using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

// Owns only handles captured during an explicit observation, never enumerates existing processes.
public sealed class ObservedProcessExitTracker(Action<DateTimeOffset, string> publish, CancellationToken token) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<(int Pid, long Created), Entry> entries = [];
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool disposed;
    private sealed class Entry { internal Task Task = Task.CompletedTask; }
    private sealed class RegistrationHandle : WaitHandle
    {
        // The owning handle stays alive until unregister completion. This wrapper must
        // not delay or duplicate its CloseHandle when the CLR releases its wait-array ref.
        internal RegistrationHandle(WaitHandle owner) => SafeWaitHandle = new SafeWaitHandle(owner.SafeWaitHandle.DangerousGetHandle(), ownsHandle: false);
    }
    public Task Failure => failure.Task;
    public int Count { get { lock (gate) return entries.Count; } }
    // Takes ownership even on rejection, so limits and cancellation cannot leak process handles.
    public void Track(int pid, long created, WaitHandle handle, Func<DateTimeOffset> exitTime, string executable)
    {
        lock (gate)
        {
            if (disposed || lifetime.IsCancellationRequested || entries.ContainsKey((pid, created))) { handle.Dispose(); return; }
            if (entries.Count >= 128) { handle.Dispose(); throw new IOException("退出观察达到 128 个并发进程上限，记录不完整。"); }
            var entry = new Entry(); entries.Add((pid, created), entry);
            entry.Task = WatchAsync((pid, created), handle, exitTime, executable);
        }
    }
    private async Task WatchAsync((int Pid, long Created) key, WaitHandle handle, Func<DateTimeOffset> exitTime, string executable)
    {
        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = lifetime.Token.Register(() => completion.TrySetCanceled(lifetime.Token));
            using var registrationHandle = new RegistrationHandle(handle);
            var wait = ThreadPool.RegisterWaitForSingleObject(registrationHandle, (_, _) => completion.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
            try { await completion.Task.ConfigureAwait(false); }
            finally
            {
                // Windows unregisters asynchronously unless given a completion event.
                // The callback only completes an asynchronous TCS and never takes our gate.
                using var unregistered = new ManualResetEvent(false);
                if (wait.Unregister(unregistered)) unregistered.WaitOne();
            }
            lifetime.Token.ThrowIfCancellationRequested();
            publish(exitTime(), executable);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { failure.TrySetException(error); }
        finally { handle.Dispose(); lock (gate) entries.Remove(key); }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (gate) { if (disposed) return; disposed = true; lifetime.Cancel(); tasks = [.. entries.Values.Select(e => e.Task)]; }
        await Task.WhenAll(tasks).ConfigureAwait(false); lifetime.Dispose();
    }
}
