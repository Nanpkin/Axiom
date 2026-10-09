namespace Axiom.Providers.Windows;

public static partial class WindowsAudioPlayback
{
    public static Task<(bool Success, string Reason)> AdjustAsync(string operation, int step, CancellationToken token)
    {
        if (operation is not ("increase" or "decrease") || step is < 1 or > 100) throw new ArgumentException("相对音量操作无效。");
        return Commands.RunAsync(cancellation => ApplyEndpoint(false, cancellation,
            endpoint => AudioPlaybackAdjustOperation.Apply(endpoint, operation, step, cancellation)), token);
    }
}
