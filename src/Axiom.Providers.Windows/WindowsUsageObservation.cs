using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Axiom.Providers.Windows;

public sealed record NativeUsageSample(DateTimeOffset AtUtc, string Kind, string? Executable);

[SupportedOSPlatform("windows")]
public static class WindowsUsageObservation
{
    public static async Task RunAsync(bool foreground, bool starts, bool systemEvents, bool exits, bool windowStates, string[] excludedApplications, Action<NativeUsageSample> publish, CancellationToken token, bool audioEvents = false)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var sessionId = currentProcess.SessionId; var ownerSid = identity.User?.Value ?? throw new UnauthorizedAccessException();
        var observationStarted = DateTimeOffset.UtcNow.ToFileTime();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var jobs = new List<Task>();
        await using var exitTracker = new ObservedProcessExitTracker((at, path) => publish(new(at, "process-exit", path)), lifetime.Token);
        if (exits) jobs.Add(exitTracker.Failure);
        using var source = starts || exits ? new WindowsProcessStartEvents([], observeAll: true) : null;
        using var window = foreground ? new WindowsForegroundEvents() : null;
        using var system = systemEvents ? new WindowsSessionEvents() : null;
        using var audio = audioEvents ? new WindowsAudioEvents() : null;
        if (audio is not null) jobs.Add(Audio());
        if (system is not null) jobs.Add(System());
        if (source is not null) jobs.Add(Starts());
        if (window is not null) jobs.Add(Windows());
        if (windowStates) jobs.Add(WindowsUsageWindows.RunAsync(excludedApplications, (at, kind, path) => publish(new(at, kind, path)), lifetime.Token));
        try { await await Task.WhenAny(jobs); }
        finally { lifetime.Cancel(); try { await Task.WhenAll(jobs.Where(t => !ReferenceEquals(t, exitTracker.Failure))); } catch (OperationCanceledException) { } }

        async Task Audio()
        {
            await foreach (var kind in audio!.Events.ReadAllAsync(lifetime.Token)) publish(new(DateTimeOffset.UtcNow, "audio-" + kind, null));
        }
        async Task System()
        {
            await foreach (var kind in system!.Events.ReadAllAsync(lifetime.Token)) publish(new(DateTimeOffset.UtcNow, "system-" + kind, null));
        }
        async Task Starts()
        {
            await foreach (var hint in source!.Events.ReadAllAsync(lifetime.Token))
                if (hint.EventTime >= observationStarted) Publish(hint.ProcessId, "process-start", hint.EventTime);
        }
        async Task Windows()
        {
            await foreach (var hint in window!.Events.ReadAllAsync(lifetime.Token)) Publish(hint.ProcessId, "foreground", hint.AtFileTime);
        }
        void Publish(int pid, string kind, long? eventTime)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var at = eventTime is { } observed ? new DateTimeOffset(DateTime.FromFileTimeUtc(observed)) : DateTimeOffset.UtcNow;
            void Boundary() { if (kind == "foreground") publish(new(at, "foreground-unobserved", null)); }
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.SessionId != sessionId) { Boundary(); return; }
                var trackExit = exits && kind == "process-start";
                var handle = OpenProcess(trackExit ? 0x101000u : 0x1000u, false, pid);
                if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (WindowsProcessIdentity.ReadOwnerSid(handle) != ownerSid) { Boundary(); return; }
                    if (!GetProcessTimes(handle, out var created, out _, out _, out _) || eventTime is { } notified && created > notified)
                        throw new InvalidOperationException("Process identity changed.");
                    var path = WindowsProcessIdentity.ReadImagePath(handle);
                    if (new[] { "Axiom.exe", "Axiom.ServiceHost.exe", "Axiom.SessionAgent.exe", "Axiom.WinUI.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) { Boundary(); return; }
                    if (excludedApplications.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) { Boundary(); return; }
                    if (kind != "process-start" || starts) publish(new(at, kind, path));
                    if (trackExit)
                    {
                        var owned = handle; handle = 0;
                        var wait = new OwnedProcessWaitHandle(owned);
                        exitTracker.Track(pid, created, wait, () =>
                        {
                            if (!GetProcessTimes(owned, out var actualCreated, out var exited, out _, out _) || actualCreated != created || exited <= 0)
                                throw new IOException("进程退出时间未确认，记录不完整。");
                            return new DateTimeOffset(DateTime.FromFileTimeUtc(exited));
                        }, path);
                    }
                }
                finally { if (handle != 0) CloseHandle(handle); }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            { publish(new(at, "gap", null)); }
        }
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern nint OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint handle,out long created,out long exited,out long kernel,out long user);
}
