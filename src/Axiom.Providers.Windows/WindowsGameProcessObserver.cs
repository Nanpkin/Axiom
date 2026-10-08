using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

public sealed record VerifiedProcessInstance(string ExecutablePath, int ProcessId,
    DateTimeOffset StartedAtUtc, int SessionId);

public sealed record ProcessPresenceChange(VerifiedProcessInstance? Instance, bool Running,
    string? Reason = null);

/// <summary>
/// Observes allowlisted processes using shared native creation events (including headless
/// apps) and waitable process handles for exact exits. Requires an elevated Agent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGameProcessObserver : IAsyncDisposable
{
    private readonly Dictionary<string, string?> names;
    private readonly Dictionary<(int Pid, DateTimeOffset Started), WaitHandle> tracked = [];
    private readonly List<Task> exits = [];
    private readonly object gate = new();
    private readonly Action<ProcessPresenceChange> publish;
    private readonly string ownerSid;
    private readonly int sessionId;
    private readonly CancellationTokenSource stopping = new();
    private WindowsProcessStartEvents? hints;
    private Task? consumer;
    private bool started;

    public WindowsGameProcessObserver(IEnumerable<string> executableNames, Action<ProcessPresenceChange> publish)
    {
        ArgumentNullException.ThrowIfNull(executableNames);
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in executableNames)
        {
            if (string.IsNullOrWhiteSpace(entry) || !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A .exe process name is required.", nameof(executableNames));
            var full = Path.IsPathFullyQualified(entry) ? Path.GetFullPath(entry) : null;
            var name = Path.GetFileNameWithoutExtension(entry);
            if (name.Length is < 1 or > 128 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                full is null && !string.Equals(entry, name + ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid executable name.", nameof(executableNames));
            names[name] = full;
        }
        ownerSid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("No current user SID.");
        sessionId = Process.GetCurrentProcess().SessionId;
    }

    public void Start()
    {
        if (started) throw new InvalidOperationException("Observer already started.");
        started = true;
        if (names.Count == 0) return;
        hints = new WindowsProcessStartEvents(names.Keys.Select(name => name + ".exe"));
        Snapshot();
        hints.ThrowIfFailed();
        consumer = ConsumeAsync();
    }

    private async Task ConsumeAsync()
    {
        // The native subscription is active before the one-time initial snapshot.
        try
        {
            await foreach (var hint in hints!.Events.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                hints.ThrowIfFailed();
                Inspect(hint.ProcessId, hint.EventTime);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (IOException error)
        {
            if (!stopping.IsCancellationRequested) publish(new(null, false, error.Message));
        }
    }

    private void Snapshot()
    {
        foreach (var name in names.Keys)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try { Inspect(process.Id); }
                finally { process.Dispose(); }
            }
        }
    }

    private void Inspect(int pid, long? eventTime = null)
    {
        // Dedupe only after reading creation time: an exited PID can already have
        // been reused while its old wait callback is still being dispatched.
        Process? process = null;
        ManualResetEvent? processExit = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.SessionId != sessionId || !names.TryGetValue(process.ProcessName, out var expectedPath)) return;
            // Some games deny PROCESS_QUERY_INFORMATION, which Process.Handle requests.
            // QUERY_LIMITED_INFORMATION is enough for image, SID and creation time;
            // SYNCHRONIZE allows an OS wait without scanning for exit.
            var handle = OpenProcess(0x00101000, false, pid);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            processExit = new ManualResetEvent(false)
            {
                SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true)
            };
            var path = WindowsProcessIdentity.ReadImagePath(handle);
            if (expectedPath is not null && !string.Equals(expectedPath, path, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(WindowsProcessIdentity.ReadOwnerSid(handle), ownerSid, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Matching process belongs to another user.");
            if (!GetProcessTimes(handle, out var created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (eventTime is { } notified && created > notified) return; // Reused PID, stale event.
            if (processExit.WaitOne(0)) return; // Never announce a process already exited during verification.
            var instance = new VerifiedProcessInstance(path, pid,
                new DateTimeOffset(DateTime.FromFileTimeUtc(created), TimeSpan.Zero), sessionId);
            var key = (instance.ProcessId, instance.StartedAtUtc);
            lock (gate)
            {
                if (tracked.ContainsKey(key)) return;
                tracked[key] = processExit;
            }
            var owned = processExit;
            processExit = null; // The exit task owns this waitable process handle.
            try
            {
                publish(new(instance, true));
                lock (gate)
                {
                    exits.RemoveAll(task => task.IsCompleted);
                    exits.Add(WatchExitAsync(key, instance, owned));
                }
            }
            catch
            {
                lock (gate) tracked.Remove(key);
                owned.Dispose();
                throw;
            }
        }
        catch (ArgumentException) { } // Process exited before it could be inspected.
        catch (InvalidOperationException) { } // Process exited before it could be inspected.
        catch (Win32Exception error)
        {
            publish(new(null, false, $"Process inspection failed for PID {pid}: {error.NativeErrorCode}"));
        }
        catch (UnauthorizedAccessException error) { publish(new(null, false, error.Message)); }
        finally { processExit?.Dispose(); process?.Dispose(); }
    }

    private async Task WatchExitAsync((int Pid, DateTimeOffset Started) key,
        VerifiedProcessInstance instance, WaitHandle processExit)
    {
        try
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = stopping.Token.Register(() => completed.TrySetCanceled(stopping.Token));
            var registration = ThreadPool.RegisterWaitForSingleObject(processExit,
                (_, _) => completed.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
            try { await completed.Task.ConfigureAwait(false); }
            finally { registration.Unregister(null); }
            if (!stopping.IsCancellationRequested)
            {
                hints?.ThrowIfFailed();
                publish(new(instance, false));
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            if (!stopping.IsCancellationRequested)
                publish(new(null, false, $"Process exit observation failed for PID {instance.ProcessId}: {error.Message}"));
        }
        finally
        {
            lock (gate) tracked.Remove(key);
            processExit.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        hints?.Dispose();
        try
        {
            if (consumer is not null) await consumer.ConfigureAwait(false);
        }
        finally
        {
            Task[] pending;
            lock (gate) pending = [.. exits];
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            finally { stopping.Dispose(); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit,
        out long kernel, out long user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
