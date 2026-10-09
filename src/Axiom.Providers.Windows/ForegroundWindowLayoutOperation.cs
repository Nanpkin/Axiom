namespace Axiom.Providers.Windows;

internal interface IForegroundLayoutWindow
{
    bool IsCurrent { get; }
    Task<bool> RestoreAsync(CancellationToken token);
    bool Place(string layout, CancellationToken token);
    Task<bool> ConfirmAsync(CancellationToken token);
}

internal static class ForegroundWindowLayoutOperation
{
    internal static async Task<(bool Success, string Reason)> ApplyAsync(IForegroundLayoutWindow window, string layout, CancellationToken token)
    {
        if (!WindowLayoutGeometry.IsLayout(layout)) throw new ArgumentException("前台窗口布局无效。");
        token.ThrowIfCancellationRequested();
        var attempted = false;
        try
        {
            if (!window.IsCurrent) return (false, "捕获的前台窗口已消失、切换或身份无法核验；未还原或移动任何窗口。");
            attempted = true;
            if (!await window.RestoreAsync(token)) return (false, "无法确认捕获窗口仍是前台或已还原；可能已还原，未请求调整布局。");
            token.ThrowIfCancellationRequested();
            if (!window.IsCurrent) return (false, "窗口已切走或身份变化；可能已还原，未请求调整布局，不会操作新的前台窗口。");
            if (!window.Place(layout, token)) return (false, "窗口身份、前台选择或工作区变化，或系统拒绝布局；可能已还原或移动，结果未确认。");
            token.ThrowIfCancellationRequested();
            if (!await window.ConfirmAsync(token)) return (false, "捕获窗口布局未读回确认，可能已还原或移动；不重试、不恢复旧布局。");
            return (true, "已整理执行开始时捕获的前台窗口，位置及大小已读回确认；之后手动调整不受干预。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { return (false, attempted ? "前台窗口布局未确认；窗口可能已还原或移动，通知、权限或读回可能失败，不重试、不恢复旧布局。" :
            "捕获的前台窗口身份无法核验，可能权限不足；未还原或移动任何窗口。"); }
    }
}
