using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
internal sealed class BrightnessRestoreTopology : IDisposable
{
    private readonly WindowsSessionEvents source = new(displayOnly: true);
    private readonly CancellationTokenSource stop = new();
    private readonly BrightnessTopologyGuard identity;
    internal BrightnessRestoreTopology(Func<string?>? readIdentity = null)
    { identity = new(readIdentity); _ = ObserveAsync(); }
    internal bool Valid => !source.Failed && identity.Valid;
    private async Task ObserveAsync()
    {
        try { await foreach (var _ in source.Events.ReadAllAsync(stop.Token)) identity.Changed(); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { identity.Invalidate(); }
    }
    public void Dispose() { stop.Cancel(); source.Dispose(); stop.Dispose(); }
}

// WM_DISPLAYCHANGE also means a resolution/HDR mode change on the same connected screen.
// Revalidate only after a notification and only on the original native worker. Missing or
// changed identity remains terminal; this never chooses another screen or captures a baseline.
internal sealed class BrightnessTopologyGuard(Func<string?>? readIdentity)
{
    private readonly string? original = Read(readIdentity);
    private int changed, checkedVersion, invalid;
    internal void Changed() => Interlocked.Increment(ref changed);
    internal void Invalidate() => Interlocked.Exchange(ref invalid, 1);
    internal bool Valid
    {
        get
        {
            if (Volatile.Read(ref invalid) != 0) return false;
            var version = Volatile.Read(ref changed);
            if (version == checkedVersion) return true;
            if (original is null || !StringComparer.OrdinalIgnoreCase.Equals(original, Read(readIdentity)))
            { Invalidate(); return false; }
            checkedVersion = version;
            return version == Volatile.Read(ref changed);
        }
    }
    private static string? Read(Func<string?>? read)
    {
        try { return read?.Invoke(); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return null; }
    }
}
