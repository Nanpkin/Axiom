namespace Axiom.Providers.Windows;

internal interface ITaskbarAutoHideApi
{
    bool IsCurrent { get; }
    uint? ReadState();
    bool SetState(uint state);
}
internal static class TaskbarAutoHideOperation
{
    internal const uint AutoHide = 1;
    internal static (bool Success, string Reason) Apply(ITaskbarAutoHideApi api, bool enabled, CancellationToken token)
    {
        var attempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!api.IsCurrent) return (false, "当前用户的系统任务栏或活动交互会话无法核验，本次未修改。");
            var prior = api.ReadState(); token.ThrowIfCancellationRequested();
            if (!prior.HasValue || !api.IsCurrent) return (false, "任务栏设置未知或捕获的系统Shell已变化，本次未修改。");
            if (((prior.Value & AutoHide) != 0) == enabled) return (true, enabled ? "任务栏已经开启自动隐藏，无需修改；鼠标移到边缘仍可显示。" : "任务栏已经关闭自动隐藏，无需修改。");
            var requested = enabled ? prior.Value | AutoHide : prior.Value & ~AutoHide;
            token.ThrowIfCancellationRequested(); attempted = true;
            if (!api.SetState(requested)) return (false, "任务栏设置请求未确认，可能已修改或Shell已变化；不重试、不恢复。");
            token.ThrowIfCancellationRequested();
            if (!api.IsCurrent) return (false, "设置后捕获的系统Shell已变化，可能已修改但结果未确认；不会转而操作新任务栏。");
            var confirmed = api.ReadState(); token.ThrowIfCancellationRequested();
            if (!confirmed.HasValue || !api.IsCurrent) return (false, "任务栏设置可能已修改，但读回或系统Shell身份未确认；不重试、不恢复。");
            if (((confirmed.Value & AutoHide) != 0) != enabled) return (false, "任务栏自动隐藏设置读回不一致，可能有手动修改或系统拒绝；不重试、不恢复。");
            return (true, enabled ? "已读回确认开启任务栏自动隐藏；鼠标移到边缘、通知或系统行为仍可展开。设置保留，不持续强制或自动恢复。" : "已读回确认关闭任务栏自动隐藏（始终显示）；设置保留，不持续强制或自动恢复。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { return (false, attempted ? "任务栏设置可能已修改但结果未确认；不重试、不恢复。" : "任务栏状态或身份无法读取，本次未修改。"); }
    }
}
