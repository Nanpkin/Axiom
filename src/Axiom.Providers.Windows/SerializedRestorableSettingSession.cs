namespace Axiom.Providers.Windows;

internal abstract class SerializedRestorableSettingSession(BoundedNativeCommand commands) : IRestorableSettingSession
{
    private readonly SemaphoreSlim serialization = new(1, 1);
    private bool disposed;
    protected abstract RestorableSettingResult ApplyCore(IReadOnlyDictionary<string, string> parameters, CancellationToken token);
    protected abstract RestorableSettingResult ReleaseCore(CancellationToken token);
    protected abstract void RelinquishCore();
    protected abstract void DisposeCore();
    public Task<RestorableSettingResult> ApplyAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken token) =>
        RunAsync(() => parameters, false, token);
    public Task<RestorableSettingResult> ReleaseAsync(CancellationToken token) => RunAsync(() => null, true, token);
    private async Task<RestorableSettingResult> RunAsync(Func<IReadOnlyDictionary<string, string>?> parameters, bool release, CancellationToken token)
    {
        await serialization.WaitAsync(token);
        try
        {
            if (disposed) return new(false, "原设置租约已结束。", false);
            RestorableSettingResult? receipt = null;
            (bool Success, string Reason) result;
            try
            {
                result = await commands.RunAsync(cancellation =>
                {
                    receipt = release ? ReleaseCore(cancellation) : ApplyCore(parameters()!, cancellation);
                    return (receipt.Success, receipt.Reason);
                }, token);
            }
            catch (OperationCanceledException) { result = (false, "设置请求已取消，可能已修改但结果未确认。" ); }
            if (receipt is not null && result.Success == receipt.Success && result.Reason == receipt.Reason) return receipt;
            // Caller timeout/cancellation is not proof the native work stopped. It still owns its gate/handles.
            RelinquishCore();
            return new(false, result.Reason, false);
        }
        finally { serialization.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await serialization.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            // No cancellation: closing a caller cannot free handles under a blocked real native call.
            await commands.DrainAsync();
            await Task.Factory.StartNew(DisposeCore, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally { serialization.Release(); }
    }
}
