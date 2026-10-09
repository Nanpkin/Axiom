namespace Axiom.Providers.Windows;

internal static class AudioPlaybackAdjustOperation
{
    internal static (bool Success, string Reason) Apply(IAudioPlaybackEndpoint endpoint, string operation, int step, CancellationToken token)
    {
        if (operation is not ("increase" or "decrease") || step is < 1 or > 100) throw new ArgumentException("相对音量操作无效。");
        token.ThrowIfCancellationRequested();
        var initial = endpoint.GetVolume();
        token.ThrowIfCancellationRequested();
        if (!float.IsFinite(initial) || initial is < 0 or > 1) return (false, "无法读取有效的默认播放音量，本次未写入；不自动重试。");
        var target = Math.Clamp(initial + (operation == "increase" ? step : -step) / 100f, 0f, 1f);
        endpoint.SetVolume(target);
        token.ThrowIfCancellationRequested();
        var actual = endpoint.GetVolume();
        token.ThrowIfCancellationRequested();
        return float.IsFinite(actual) && actual is >= 0 and <= 1 && Math.Abs(actual - target) <= 0.005f
            ? (true, $"执行开始时的默认播放设备音量已从{initial * 100:F0}%调整到{actual * 100:F0}%；静音状态未更改。")
            : (false, "相对音量写入后读回不一致，可能被设备或其他操作改变；不重试、不恢复旧值。");
    }
}
