using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsAudioPlayback
{
    public static Task<(bool Success, string Reason)> ToggleMicrophoneAsync(CancellationToken token) =>
        Commands.RunAsync(cancellation => ApplyEndpoint(true, cancellation,
            endpoint => MicrophoneToggleOperation.Apply(endpoint, cancellation)), token);
}
