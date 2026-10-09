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
    internal static (bool Success, string Reason) Apply(IAudioPlaybackEndpoint endpoint, string operation, int percent, CancellationToken token, string endpointName = "默认播放设备")
    {
        if (operation is not ("volume" or "mute" or "unmute") || percent is < 0 or > 100) throw new ArgumentException("音量操作无效。");
        token.ThrowIfCancellationRequested();
        if (operation == "volume")
        {
            endpoint.SetVolume(percent / 100f); token.ThrowIfCancellationRequested();
            var actual = endpoint.GetVolume();
            return float.IsFinite(actual) && Math.Abs(actual - percent / 100f) <= 0.005f
                ? (true, $"执行开始时的{endpointName}音量已设为{percent}%；静音状态未更改。")
                : (false, "音量写入后读回不一致，可能被设备或其他操作改变；不重试、不恢复旧值。");
        }
        var muted = operation == "mute";
        endpoint.SetMuted(muted); token.ThrowIfCancellationRequested();
        return endpoint.GetMuted() == muted
            ? (true, muted ? $"执行开始时的{endpointName}已静音；音量未更改。" : $"执行开始时的{endpointName}已取消静音；音量未更改。")
            : (false, "静音写入后读回不一致，可能被其他操作改变；不重试、不恢复旧值。");
    }
}
