namespace Axiom.Providers.Windows;

internal static class MicrophoneToggleOperation
{
    internal static (bool Success, string Reason) Apply(IAudioPlaybackEndpoint endpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool current;
        try { current = endpoint.GetMuted(); }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        { return (false, "无法读取默认通信麦克风当前静音状态，可能无设备或权限不足；本次未修改，不重试。"); }
        token.ThrowIfCancellationRequested();
        var desired = !current;
        try
        {
            endpoint.SetMuted(desired);
            token.ThrowIfCancellationRequested();
            var actual = endpoint.GetMuted();
            token.ThrowIfCancellationRequested();
            return actual == desired
                ? (true, desired ? "执行开始时的默认通信麦克风已切换为静音；输入音量未更改。" : "执行开始时的默认通信麦克风已切换为取消静音，可能立即恢复收音；输入音量未更改。")
                : (false, "麦克风切换后读回不一致，可能被设备或其他操作改变；不重试、不恢复旧状态。");
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        { return (false, "麦克风切换或读回失败，可能设备不可用或权限不足；可能已修改但结果未确认，不重试、不恢复旧状态。"); }
    }
}
