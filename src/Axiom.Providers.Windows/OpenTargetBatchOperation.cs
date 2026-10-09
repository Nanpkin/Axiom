namespace Axiom.Providers.Windows;

internal interface IOpenTargetBatchApi
{
    bool CanOpen(string kind, string target, CancellationToken token);
    bool Open(string kind, string target, CancellationToken token);
}

internal sealed class OpenTargetBatchProgress(int total)
{
    private int accepted, sending;
    internal void Sending(int index) => Volatile.Write(ref sending, index);
    internal void Accepted(int index) { Volatile.Write(ref accepted, index); Volatile.Write(ref sending, 0); }
    internal void Refused() => Volatile.Write(ref sending, 0);
    internal string Describe()
    {
        var confirmed = Volatile.Read(ref accepted); var pending = Volatile.Read(ref sending);
        return $"已确认系统受理{confirmed}/{total}项；" + (pending > confirmed ? $"第{pending}项可能已受理但结果未确认；" : "") +
            "整批上限8秒，不重试、不自动关闭已打开的窗口；受理不代表内容载入完成。";
    }
}

internal static class OpenTargetBatchOperation
{
    internal static (bool Success, string Reason) Apply((string Kind, string Target)[] targets, IOpenTargetBatchApi api,
        OpenTargetBatchProgress progress, CancellationToken token)
    {
        if (targets.Length is < 1 or > 8) throw new ArgumentException("工作目标数量须为1–8项。");
        try
        {
            // All configured targets and local availability are checked before any shell request.
            for (var i = 0; i < targets.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!api.CanOpen(targets[i].Kind, targets[i].Target, token)) return (false, $"第{i + 1}项预检失败，目标不可访问或不受支持；整组未请求打开。");
            }
            for (var i = 0; i < targets.Length; i++)
            {
                token.ThrowIfCancellationRequested(); progress.Sending(i + 1);
                if (!api.Open(targets[i].Kind, targets[i].Target, token))
                { progress.Refused(); return (false, $"Windows未接受第{i + 1}项打开请求，已停止后续项。"); }
                progress.Accepted(i + 1); token.ThrowIfCancellationRequested();
            }
            return (true, "Windows已接受整组打开请求。");
        }
        catch (OperationCanceledException) { return (false, "工作目标打开已取消，已停止后续项。"); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { return (false, "工作目标打开未确认完成，已停止后续项；不自动重试。"); }
    }
}
