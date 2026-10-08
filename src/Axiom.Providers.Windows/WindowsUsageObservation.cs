using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record NativeUsageSample(DateTimeOffset AtUtc, string Kind, string? Executable);

[SupportedOSPlatform("windows")]
public static class WindowsUsageObservation
{
    public static async Task RunAsync(bool foreground, bool starts, bool systemEvents, Action<NativeUsageSample> publish, CancellationToken token)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var sessionId = currentProcess.SessionId; var ownerSid = identity.User?.Value ?? throw new UnauthorizedAccessException();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var jobs = new List<Task>();
        using var source = starts ? new WindowsProcessStartEvents([], observeAll: true) : null;
        using var window = foreground ? new ForegroundSource() : null;
        using var system = systemEvents ? new WindowsSessionEvents() : null;
        if (system is not null) jobs.Add(System());
        if (source is not null) jobs.Add(Starts());
        if (window is not null) jobs.Add(Windows());
        try { await await Task.WhenAny(jobs); }
        finally { lifetime.Cancel(); try { await Task.WhenAll(jobs); } catch (OperationCanceledException) { } }

        async Task System()
        {
            await foreach (var kind in system!.Events.ReadAllAsync(lifetime.Token)) publish(new(DateTimeOffset.UtcNow, "system-" + kind, null));
        }
        async Task Starts()
        {
            await foreach (var hint in source!.Events.ReadAllAsync(lifetime.Token))
                Publish(hint.ProcessId, "process-start", hint.EventTime);
        }
        async Task Windows()
        {
            await foreach (var pid in window!.Events.Reader.ReadAllAsync(lifetime.Token)) Publish(pid, "foreground", null);
        }
        void Publish(int pid, string kind, long? eventTime)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var at = DateTimeOffset.UtcNow;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.SessionId != sessionId) return;
                var handle = OpenProcess(0x1000, false, pid);
                if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (WindowsGameProcessObserver.ReadOwnerSid(handle) != ownerSid) return;
                    if (eventTime is { } notified && (!GetProcessTimes(handle, out var created, out _, out _, out _) || created > notified))
                        throw new InvalidOperationException("Process identity changed.");
                    var path = WindowsGameProcessObserver.ReadImagePath(handle);
                    if (Path.GetFileName(path).StartsWith("Axiom", StringComparison.OrdinalIgnoreCase)) return;
                    publish(new(at, kind, path));
                }
                finally { CloseHandle(handle); }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            { publish(new(at, "gap", null)); }
        }
    }
    private sealed class ForegroundSource : IDisposable
    {
        internal readonly Channel<int> Events = Channel.CreateBounded<int>(128);
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread thread;
        private readonly Callback callback;
        private uint id;
        internal ForegroundSource()
        {
            callback = (_, _, hwnd, _, _, _, _) => Enqueue(hwnd);
            thread = new Thread(Run) { IsBackground = true, Name = "Axiom opt-in observation" }; thread.Start(); ready.Task.GetAwaiter().GetResult();
        }
        private void Enqueue(nint window)
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid != 0 && !Events.Writer.TryWrite((int)pid)) Events.Writer.TryComplete(new IOException("观察事件队列溢出。"));
        }
        private void Run()
        {
            id = GetCurrentThreadId(); PeekMessage(out _, 0, 0, 0, 0);
            var hook = SetWinEventHook(3, 3, 0, callback, 0, 0, 0);
            if (hook == 0) { ready.TrySetException(new Win32Exception()); return; }
            try { Enqueue(GetForegroundWindow()); ready.TrySetResult(); while (GetMessage(out _, 0, 0, 0) > 0) { } }
            finally { UnhookWinEvent(hook); Events.Writer.TryComplete(); }
        }
        public void Dispose() { PostThreadMessage(id, 0x12, 0, 0); thread.Join(TimeSpan.FromSeconds(3)); }
    }
    private delegate void Callback(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W,L; public uint Time; public int X,Y; public uint Private; }
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SetWinEventHook(uint min,uint max,nint module,Callback callback,uint pid,uint tid,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message,nint window,uint min,uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message,nint window,uint min,uint max,uint flags);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread,uint message,nint w,nint l);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern nint OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint handle,out long created,out long exited,out long kernel,out long user);
}
