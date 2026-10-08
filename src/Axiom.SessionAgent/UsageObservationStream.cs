using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal static class UsageObservationStream
{
    private static event Action<UsageSample>? ActionStarted;
    internal static void MarkAction(Axiom.Domain.TaskInvocation? invocation = null)
    {
        var listener = ActionStarted; if (listener is null) return;
        string[] paths = [];
        if (invocation?.Parameters.TryGetValue("executable", out var executable) == true)
        {
            try { paths = ExecutablePaths.Parse(executable); } catch (ArgumentException) { }
        }
        if (paths.Length == 0) listener(new(DateTimeOffset.UtcNow, "axiom-action", Origin: "axiom"));
        else foreach (var path in paths) listener(new(DateTimeOffset.UtcNow, "axiom-action", path, "axiom"));
    }
    internal static async Task RunAsync(Stream pipe, ProviderMessage request, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = Channel.CreateBounded<UsageSample>(256);
        void Publish(UsageSample sample) { if (!queue.Writer.TryWrite(sample)) queue.Writer.TryComplete(new IOException("观察队列溢出，记录已停止。")); }
        ActionStarted += Publish;
        var native = WindowsUsageObservation.RunAsync(request.UsageOptions!.Foreground, request.UsageOptions.ProcessStarts, request.UsageOptions.SystemEvents, request.UsageOptions.ProcessExits, request.UsageOptions.WindowStates, request.UsageOptions.ExcludedApplications,
            sample => Publish(new(sample.AtUtc, sample.Kind, sample.Executable)), lifetime.Token);
        var disconnected = ProviderProtocol.ReadAsync(pipe, lifetime.Token);
        try
        {
            if (native.IsCompleted) { await native; throw new IOException("观察源未能开始。"); }
            await ProviderProtocol.WriteAsync(pipe, new(1, "usage-ready", request.RequestId), lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                var next = queue.Reader.ReadAsync(lifetime.Token).AsTask();
                if (await Task.WhenAny(next, native, disconnected) != next) { if (native.IsCompleted) await native; break; }
                var sample = await next;
                using var write = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); write.CancelAfter(TimeSpan.FromSeconds(5));
                await ProviderProtocol.WriteAsync(pipe, new(1, "usage-event", request.RequestId, UsageSample: sample), write.Token);
            }
        }
        finally
        {
            ActionStarted -= Publish; lifetime.Cancel();
            try { await native; } catch (OperationCanceledException) { }
            try { await disconnected; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }
}
