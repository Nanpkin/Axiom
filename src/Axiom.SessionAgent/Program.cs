using Axiom.Providers.Windows;
using System.Security.Principal;

namespace Axiom.SessionAgent;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (args is ["--run"])
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("No user SID.");
            using var single = new Mutex(true, @"Local\Axiom.GameHdr.Agent." + sid, out var firstAgent);
            if (!firstAgent) throw new InvalidOperationException("Axiom session agent is already running.");
            await using var agent = new GameHdrAgent(sid);
            Console.WriteLine("Axiom game HDR session agent running. Press Ctrl+C to stop.");
            await agent.RunAsync();
            return;
        }
        if (args is ["--probe-hdr"])
        {
            foreach (var display in WindowsHdrDisplayProbe.ReadActiveDisplays())
                Console.WriteLine($"{display.FriendlyName} [{display.Id}]: supported={display.Supported}, enabled={display.UserEnabled}, active={display.Active}, error={display.Error}");
            return;
        }
        if (args is ["--probe-window-events"])
        {
            using var hints = new WindowsWindowProcessHints();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                await foreach (var pid in hints.ProcessIds.ReadAllAsync(timeout.Token))
                    Console.WriteLine($"Window process hint: {pid}");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            Console.WriteLine($"Gap={hints.TakeGap()}");
            return;
        }
        if (args is ["--probe-process", var executable])
        {
            await using var observer = new WindowsGameProcessObserver([executable], change =>
                Console.WriteLine(change.Instance is { } instance
                    ? $"Verified process: {instance.ProcessId} running={change.Running} path={instance.ExecutablePath}"
                    : $"Process unknown: {change.Reason}"));
            observer.Start();
            await Task.Delay(TimeSpan.FromSeconds(12));
            return;
        }
        if (args is ["--watch-process", .. var executables] && executables.Length > 0)
        {
            await using var observer = new WindowsGameProcessObserver(executables, change =>
                Console.WriteLine(change.Instance is { } instance
                    ? $"{DateTimeOffset.Now:O} {instance.ExecutablePath} PID {instance.ProcessId}: {(change.Running ? "running" : "exited")}"
                    : $"{DateTimeOffset.Now:O} Unknown: {change.Reason}"));
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopped.TrySetResult(); };
            observer.Start();
            Console.WriteLine("Watching configured executable paths. Press Ctrl+C to stop. HDR is not changed.");
            await stopped.Task;
            return;
        }
        throw new ArgumentException("Agent run mode is not connected yet.");
    }
}
