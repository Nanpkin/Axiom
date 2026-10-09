using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsAudioPlayback
{
    public static Task<(bool Success, string Reason)> TogglePlaybackAsync(CancellationToken token) =>
        Commands.RunAsync(cancellation => ApplyEndpoint(false, cancellation,
            endpoint => AudioPlaybackToggleOperation.Apply(endpoint, cancellation)), token);
    public static Task DrainAsync() => Commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
}
