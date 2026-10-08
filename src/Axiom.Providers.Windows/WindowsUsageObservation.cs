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
        var observationStarted = DateTimeOffset.UtcNow.ToFileTime();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var jobs = new List<Task>();
        using var source = starts ? new WindowsProcessStartEvents([], observeAll: true) : null;
        using var window = foreground ? new WindowsForegroundEvents() : null;
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
                var handle = OpenProcess(0x1000, false, pid);
                if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (WindowsProcessIdentity.ReadOwnerSid(handle) != ownerSid) { Boundary(); return; }
                    if (eventTime is { } notified && (!GetProcessTimes(handle, out var created, out _, out _, out _) || created > notified))
                        throw new InvalidOperationException("Process identity changed.");
                    var path = WindowsProcessIdentity.ReadImagePath(handle);
                    if (new[] { "Axiom.exe", "Axiom.ServiceHost.exe", "Axiom.SessionAgent.exe", "Axiom.WinUI.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) { Boundary(); return; }
                    publish(new(at, kind, path));
                }
                finally { CloseHandle(handle); }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            { publish(new(at, "gap", null)); }
        }
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern nint OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint handle,out long created,out long exited,out long kernel,out long user);
}
