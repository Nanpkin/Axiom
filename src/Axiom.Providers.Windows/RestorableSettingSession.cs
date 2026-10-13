namespace Axiom.Providers.Windows;

// A receipt describes this owned transaction, never the machine's global state.
public sealed record RestorableSettingResult(bool Success, string Reason, bool Owned);

// Implementations pin the actual target and its Known first baseline before any write.
// Updates retain that baseline; failed/unconfirmed writes or external change relinquish ownership.
// Release uses a separate cleanup token, compares the last confirmed value, and reports readback.
// Native admission and handles remain held until the real work and cleanup have finished.
public interface IRestorableSettingSession : IAsyncDisposable
{
    Task<RestorableSettingResult> ApplyAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken token);
    Task<RestorableSettingResult> ReleaseAsync(CancellationToken token);
}
