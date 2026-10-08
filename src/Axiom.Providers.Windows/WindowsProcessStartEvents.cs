using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record ProcessStartHint(int ProcessId, long EventTime);

/// <summary>One native, non-polling WMI subscription per Agent, leased only while needed.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessStartEvents : IDisposable
{
    private static readonly object Gate = new();
    private static readonly HashSet<WindowsProcessStartEvents> Clients = [];
    private static ManagementEventWatcher? watcher;
    private static Exception? sourceFailure;
    private static long receivedEvents;
    private static long routedHints;
    private readonly HashSet<string> names;
    private readonly Channel<ProcessStartHint> queue = Channel.CreateBounded<ProcessStartHint>(
        new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private bool disposed;
    private readonly bool observeAll;
    private Exception? failure;

    public ChannelReader<ProcessStartHint> Events => queue.Reader;
    public void ThrowIfFailed()
    {
        if (Volatile.Read(ref failure) is { } error) throw new IOException(error.Message, error);
    }
    public static int ActiveConsumers { get { lock (Gate) return Clients.Count; } }
    public static bool IsSubscribed { get { lock (Gate) return watcher is not null; } }
    public static (long Received, long Routed) Counters => (Interlocked.Read(ref receivedEvents), Interlocked.Read(ref routedHints));

    public WindowsProcessStartEvents(IEnumerable<string> executableNames, bool observeAll = false)
    {
        this.observeAll = observeAll;
        names = executableNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0 && !observeAll) throw new ArgumentException("At least one process name is required.", nameof(executableNames));
        lock (Gate)
        {
            if (sourceFailure is not null) throw new InvalidOperationException("Process event source is reconnecting.", sourceFailure);
            Clients.Add(this);
            if (watcher is not null) return;
            var query = $"SELECT * FROM Win32_ProcessStartTrace WHERE SessionID = {Process.GetCurrentProcess().SessionId}";
            var candidate = new ManagementEventWatcher(new ManagementScope(@"\\.\root\cimv2"), new WqlEventQuery(query));
            candidate.EventArrived += Arrived;
            candidate.Stopped += Stopped;
            try { candidate.Start(); watcher = candidate; }
            catch (Exception error)
            {
                Clients.Remove(this); candidate.EventArrived -= Arrived; candidate.Stopped -= Stopped; candidate.Dispose();
                if (error is UnauthorizedAccessException || error is ManagementException { ErrorCode: ManagementStatus.AccessDenied })
                    throw new UnauthorizedAccessException("无法订阅 Windows 进程通知，请以管理员身份启动 Axiom。", error);
                throw new IOException("无法订阅 Windows 原生进程通知，检测状态未知。", error);
            }
        }
    }

    private static void Arrived(object sender, EventArrivedEventArgs args)
    {
        try
        {
            using var item = args.NewEvent;
            var name = (string)item["ProcessName"];
            var pid = checked((int)(uint)item["ProcessID"]);
            var time = checked((long)(ulong)item["TIME_CREATED"]);
            Interlocked.Increment(ref receivedEvents);
            lock (Gate)
            {
                foreach (var client in Clients)
                    if (Volatile.Read(ref client.failure) is null && (client.observeAll || client.names.Contains(name)))
                    {
                        Interlocked.Increment(ref routedHints);
                        if (!client.queue.Writer.TryWrite(new(pid, time)))
                            client.FailClient(new IOException("Windows 进程通知队列已溢出，检测状态未知。"));
                    }
            }
        }
        catch (Exception error) when (error is ManagementException or InvalidCastException or OverflowException or ArgumentException)
        { Fail(new IOException("Windows 进程通知无法解析，检测状态未知。", error)); }
    }

    private static void Stopped(object sender, StoppedEventArgs args)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(watcher, sender)) return; // Intentional disposal already detached it.
            Fail(new IOException($"Windows 进程通知停止：{args.Status}，检测状态未知。"));
        }
    }

    private static void Fail(Exception error)
    {
        lock (Gate)
        {
            sourceFailure = error;
            foreach (var client in Clients) client.FailClient(error);
        }
    }

    private void FailClient(Exception error)
    {
        Interlocked.CompareExchange(ref failure, error, null);
        queue.Writer.TryComplete(error); // The flag is checked BEFORE draining queued hints.
    }

    public void Dispose()
    {
        ManagementEventWatcher? release = null;
        lock (Gate)
        {
            if (disposed) return;
            disposed = true;
            Clients.Remove(this); queue.Writer.TryComplete();
            if (Clients.Count == 0) { release = watcher; watcher = null; sourceFailure = null; }
        }
        if (release is not null)
        {
            release.EventArrived -= Arrived; release.Stopped -= Stopped;
            try { release.Stop(); }
            catch (ManagementException) { } // An already failed source still must be disposed.
            finally { release.Dispose(); }
        }
    }
}
