namespace Axiom.Providers.Windows;

internal enum SessionLockState { Unknown, NonInteractive, Locked, Unlocked }
internal interface ISessionLockApi
{
    SessionLockState GetCurrentState();
    (bool Accepted, int Error) RequestLock();
}

internal static class SessionLockOperation
{
    internal static (bool Success, string Reason) Apply(ISessionLockApi api, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = api.GetCurrentState();
        token.ThrowIfCancellationRequested();
        if (state == SessionLockState.Locked) return (false, "当前Windows会话已经锁定，本次未发送重复请求。");
        if (state == SessionLockState.NonInteractive) return (false, "当前会话不是活动交互会话，本次未请求锁屏；请在正常登录的用户桌面运行后台。");
        if (state != SessionLockState.Unlocked) return (false, "无法确认当前会话是否可锁定，本次未请求；请检查会话状态与读取权限。");
        token.ThrowIfCancellationRequested();
        var result = api.RequestLock();
        token.ThrowIfCancellationRequested();
        if (!result.Accepted) return (false, result.Error == 5
            ? "Windows未接受锁定请求：权限不足。不自动重试。"
            : "Windows未接受当前会话的锁定请求；会话可能已锁定、已离开交互桌面或被系统策略限制。不自动重试。");
        return (true, "Windows已接受当前会话的锁定请求，是否完成锁定未确认；请正常登录解锁，Axiom不会自动解锁。");
    }
}
