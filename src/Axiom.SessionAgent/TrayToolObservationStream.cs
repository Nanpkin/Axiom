using System.IO.Pipes;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal static class TrayToolObservationStream
{
    public static async Task RunAsync(NamedPipeServerStream pipe, ProviderMessage request, CancellationToken token)
    {
        var paths = TrayCapabilities.ValidateTargets(request.Invocation!);
        var resource = new ProviderResource(TrayTool.Trigger, ExecutablePaths.ResourceId(paths));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = Channel.CreateBounded<(bool Snapshot, long Counter, bool Active)>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        // Preserve the initial baseline separately from the bounded stream of later invalidations.
        var initial = new TaskCompletionSource<(long Counter, bool Active)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = Task.Run(() => WindowsTrayActions.WatchTargetsAsync(paths, (snapshot, counter, active) =>
        {
            if (snapshot) initial.TrySetResult((counter, active)); else queue.Writer.TryWrite((false, counter, active));
        }, lifetime.Token), lifetime.Token);
        var disconnected = ProviderProtocol.ReadAsync(pipe, lifetime.Token);
        long sequence = 0;
        try
        {
            if (await Task.WhenAny(initial.Task, watcher, disconnected) != initial.Task) { if (watcher.IsCompleted) await watcher; return; }
            var baseline = await initial.Task;
            await Send(true, baseline.Counter, baseline.Active);
            while (!lifetime.IsCancellationRequested)
            {
                var next = queue.Reader.ReadAsync(lifetime.Token).AsTask();
                if (await Task.WhenAny(next, watcher, disconnected) != next) break;
                var update = await next; await Send(false, update.Counter, update.Active);
            }
            if (watcher.IsFaulted) await watcher;
        }
        finally
        {
            lifetime.Cancel();
            try { await watcher; } catch (OperationCanceledException) { }
            try { await disconnected; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
        async Task Send(bool snapshot, long counter, bool active)
        {
            using var write = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); write.CancelAfter(TimeSpan.FromSeconds(5));
            await ProviderProtocol.WriteAsync(pipe, new(1, snapshot ? "snapshot" : "update", request.RequestId, Sequence: sequence++,
                Samples: [new(resource, StateQuality.Known, "axiom.event-count", 1, Boolean: active, Number: counter)]), write.Token);
        }
    }
}
