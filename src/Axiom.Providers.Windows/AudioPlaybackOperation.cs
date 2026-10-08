namespace Axiom.Providers.Windows;

internal interface IAudioPlaybackEndpoint
{
    void SetVolume(float value);
    float GetVolume();
    void SetMuted(bool value);
    bool GetMuted();
}
internal static class AudioPlaybackOperation
{
    internal static (bool Success, string Reason) Apply(IAudioPlaybackEndpoint endpoint, string operation, int percent, CancellationToken token)
    {
        if (operation is not ("volume" or "mute" or "unmute") || percent is < 0 or > 100) throw new ArgumentException("音量操作无效。");
        token.ThrowIfCancellationRequested();
        if (operation == "volume")
        {
            endpoint.SetVolume(percent / 100f); token.ThrowIfCancellationRequested();
            var actual = endpoint.GetVolume();
            return float.IsFinite(actual) && Math.Abs(actual - percent / 100f) <= 0.005f
                ? (true, $"执行开始时的默认播放设备音量已设为{percent}%；静音状态未更改。")
                : (false, "音量写入后读回不一致，可能被设备或其他操作改变；不重试、不恢复旧值。");
        }
        var muted = operation == "mute";
        endpoint.SetMuted(muted); token.ThrowIfCancellationRequested();
        return endpoint.GetMuted() == muted
            ? (true, muted ? "执行开始时的默认播放设备已静音；音量未更改。" : "执行开始时的默认播放设备已取消静音；音量未更改。")
            : (false, "静音写入后读回不一致，可能被其他操作改变；不重试、不恢复旧值。");
    }
}
internal sealed class BoundedAudioCommand
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal async Task<(bool Success, string Reason)> RunAsync(Func<CancellationToken, (bool Success, string Reason)> action, CancellationToken token, TimeProvider? timeProvider = null)
    {
        token.ThrowIfCancellationRequested();
        if (!gate.Wait(0)) return (false, "上一次音频操作仍未结束，本次未执行；不排队或创建更多音频线程。");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var work = Task.Run(() =>
        {
            try { lifetime.Token.ThrowIfCancellationRequested(); return action(lifetime.Token); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { return (false, "音频操作未确认完成；设备可能不可用、权限不足或操作已取消。不自动重试。"); }
            finally { gate.Release(); }
        });
        try { return await work.WaitAsync(TimeSpan.FromSeconds(5), timeProvider ?? TimeProvider.System, token); }
        catch (TimeoutException) { return (false, "音频操作超时，可能已经修改但结果未确认；不自动重试。"); }
        finally { lifetime.Cancel(); }
    }
}
