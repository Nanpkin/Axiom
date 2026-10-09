namespace Axiom.Providers.Windows;

internal interface IDisplayOffApi
{
    SessionLockState GetCurrentState();
    (bool Accepted, int Error) RequestDisplayOff();
}

internal static class DisplayOffOperation
{
    internal static (bool Success, string Reason) Apply(IDisplayOffApi api, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = api.GetCurrentState();
        token.ThrowIfCancellationRequested();
        if (state is not (SessionLockState.Locked or SessionLockState.Unlocked))
            return (false, "无法核验当前用户的活动交互会话，本次未发送屏幕关闭请求；请检查会话状态与读取权限。");
        var result = api.RequestDisplayOff();
        token.ThrowIfCancellationRequested();
        if (!result.Accepted) return (false, result.Error == 5
            ? "Windows未接受屏幕关闭请求：权限不足。不自动重试。"
            : "Windows未确认接受屏幕关闭请求，可能已发送但结果未知；不自动重试。");
        return (true, "Windows已接受一次屏幕关闭请求，物理屏幕是否关闭未确认；电脑与后台工作未被暂停。输入可能唤醒屏幕，驱动或保持屏幕唤醒请求可能影响效果。");
    }
}
