namespace Axiom.Providers.Windows;

internal readonly record struct WorkPreparationStepResult(bool Success, bool Skipped = false, string? Reason = null);
internal interface IWorkPreparationApi
{
    bool CanExecute(string kind, string target, CancellationToken token);
    WorkPreparationStepResult Execute(string kind, string target, CancellationToken token);
}

internal sealed class WorkPreparationProgress(int total)
{
    private int completed, skipped, sending, checking;
    internal void Checking(int index) => Volatile.Write(ref checking, index);
    internal void Sending(int index) => Volatile.Write(ref sending, index);
    internal void Completed(int index, bool wasSkipped)
    { if (wasSkipped) Interlocked.Increment(ref skipped); Volatile.Write(ref completed, index); Volatile.Write(ref sending, 0); }
    internal string Describe()
    {
        var done = Volatile.Read(ref completed); var pending = Volatile.Read(ref sending); var preflight = Volatile.Read(ref checking);
        return $"已确认完成{done}/{total}步（其中已运行跳过{Volatile.Read(ref skipped)}步）；" +
            (preflight > 0 ? $"第{preflight}步预检未确认，整组尚未请求执行；" : "") +
            (pending > done ? $"第{pending}步可能已受理但结果未确认；" : "") +
            "整组上限8秒，不关闭/回滚/重试；受理不代表工具就绪或资料载入。";
    }
}

internal static class WorkPreparationOperation
{
    internal static (bool Success, string Reason) Apply((string Kind, string Target)[] steps, IWorkPreparationApi api, WorkPreparationProgress progress, CancellationToken token)
    {
        if (steps.Length is < 1 or > 8) throw new ArgumentException("工作准备须为1–8步。");
        try
        {
            for (var i = 0; i < steps.Length; i++)
            {
                token.ThrowIfCancellationRequested(); progress.Checking(i + 1);
                var available = api.CanExecute(steps[i].Kind, steps[i].Target, token);
                progress.Checking(0);
                if (!available) return (false, $"第{i + 1}步预检失败，目标不可访问或不受支持；整组未执行。");
            }
            for (var i = 0; i < steps.Length; i++)
            {
                token.ThrowIfCancellationRequested(); progress.Sending(i + 1);
                var result = api.Execute(steps[i].Kind, steps[i].Target, token);
                if (!result.Success) return (false, $"第{i + 1}步未确认成功，已停止后续步骤。" + (string.IsNullOrWhiteSpace(result.Reason) ? "" : " " + result.Reason));
                progress.Completed(i + 1, result.Skipped); token.ThrowIfCancellationRequested();
            }
            return (true, "工作准备步骤已全部受理或按已运行跳过。");
        }
        catch (OperationCanceledException) { return (false, "工作准备已取消，已停止后续步骤。"); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { return (false, "工作准备未确认完成，已停止后续步骤；不自动重试。"); }
    }
}
