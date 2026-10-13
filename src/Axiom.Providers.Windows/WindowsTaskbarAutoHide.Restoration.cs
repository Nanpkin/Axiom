namespace Axiom.Providers.Windows;

public static partial class WindowsTaskbarAutoHide
{
    public static async Task<IRestorableSettingSession> OpenRestoreAsync(IReadOnlyDictionary<string, string> parameters, string sid, CancellationToken token)
    {
        ValidateState(parameters);
        ShellSession? captured = null;
        (bool Success, string Reason) result;
        try { result = await Commands.RunAsync(cancellation =>
        {
            cancellation.ThrowIfCancellationRequested();
            // The session outlives this request; each apply/release has its own independent cancellation token.
            var api = new ShellApi(sid, CancellationToken.None);
            try { captured = new(api); cancellation.ThrowIfCancellationRequested(); return (true, "已读取本轮原任务栏设置，尚未修改。"); }
            catch { if (captured is null) api.Dispose(); throw; }
        }, token); }
        catch (OperationCanceledException)
        {
            await Commands.DrainAsync();
            if (captured is not null) await captured.DisposeAsync();
            throw;
        }
        if (result.Success && captured is not null) return captured;
        // A caller deadline cannot close the process handle under still-running shell work.
        await Commands.DrainAsync();
        if (captured is not null) await captured.DisposeAsync();
        throw new IOException(result.Reason);
    }
    private static bool ValidateState(IReadOnlyDictionary<string, string> parameters) => parameters.TryGetValue("state", out var state) ? state switch
    {
        "enable" => true, "disable" => false, _ => throw new ArgumentException("请选择开启或关闭任务栏自动隐藏。")
    } : throw new ArgumentException("任务栏自动隐藏设置缺失。");
    private sealed class ShellSession : SerializedRestorableSettingSession
    {
        private readonly ShellApi api;
        private readonly TaskbarAutoHideRestoration setting;
        internal ShellSession(ShellApi api) : base(Commands) { this.api = api; setting = new(api); }
        protected override RestorableSettingResult ApplyCore(IReadOnlyDictionary<string, string> parameters, CancellationToken token) => setting.Apply(ValidateState(parameters), token);
        protected override RestorableSettingResult ReleaseCore(CancellationToken token) => setting.Release(token);
        protected override void RelinquishCore() => setting.Relinquish();
        protected override void DisposeCore() => api.Dispose();
    }
}
