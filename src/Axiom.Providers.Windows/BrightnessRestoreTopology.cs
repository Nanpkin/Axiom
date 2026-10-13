using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
internal sealed class BrightnessRestoreTopology : IDisposable
{
    private readonly WindowsSessionEvents source = new(displayOnly: true);
    private readonly CancellationTokenSource stop = new();
    private int invalid;
    internal BrightnessRestoreTopology() { _ = ObserveAsync(); }
    internal bool Valid => Volatile.Read(ref invalid) == 0 && !source.Failed;
    private async Task ObserveAsync()
    {
        try { await foreach (var _ in source.Events.ReadAllAsync(stop.Token)) Interlocked.Exchange(ref invalid, 1); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { Interlocked.Exchange(ref invalid, 1); }
    }
    public void Dispose() { stop.Cancel(); source.Dispose(); stop.Dispose(); }
}
