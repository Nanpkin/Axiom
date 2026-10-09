using System.ComponentModel;

namespace Axiom.Providers.Windows;

internal enum PriorityProcessIdentity { Owned, Other, Missing, Unknown }
internal interface IPriorityProcess
{
    PriorityProcessIdentity Identify(out string? path);
    uint GetPriority();
    void SetPriority(uint priority);
}

internal static class ProgramPriorityOperation
{
    internal static uint Class(string priority) => priority switch
    {
        "below-normal" => 0x4000, "normal" => 0x20, "above-normal" => 0x8000,
        _ => throw new ArgumentException("不支持此程序优先级。")
    };

    internal static (bool Success, string Reason) Apply(IReadOnlyCollection<IPriorityProcess> processes, IReadOnlyCollection<string> paths,
        string priority, CancellationToken token)
    {
        var desired = Class(priority);
        var targets = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, _ => (Confirmed: 0, Failed: 0), StringComparer.OrdinalIgnoreCase);
        var unknown = 0; var denied = 0;
        foreach (var process in processes)
        {
            token.ThrowIfCancellationRequested();
            string? path = null;
            try
            {
                var identity = process.Identify(out path);
                if (identity == PriorityProcessIdentity.Unknown) { unknown++; continue; }
                if (identity != PriorityProcessIdentity.Owned || path is null || !targets.ContainsKey(path)) continue;
                // Confirm the pinned creation identity immediately before touching this instance.
                if (process.Identify(out var verified) != PriorityProcessIdentity.Owned || !string.Equals(path, verified, StringComparison.OrdinalIgnoreCase))
                { Fail(path); continue; }
                var current = process.GetPriority();
                if (current == 0) { Fail(path); continue; }
                token.ThrowIfCancellationRequested();
                if (current != desired) process.SetPriority(desired);
                // A write is only confirmed by reading the same pinned process, never by reopening its PID.
                if (process.Identify(out verified) != PriorityProcessIdentity.Owned || !string.Equals(path, verified, StringComparison.OrdinalIgnoreCase) || process.GetPriority() != desired)
                { Fail(path); continue; }
                var count = targets[path]; targets[path] = (count.Confirmed + 1, count.Failed);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                if (error is UnauthorizedAccessException || error is Win32Exception { NativeErrorCode: 5 }) denied++;
                if (path is not null && targets.ContainsKey(path)) Fail(path); else unknown++;
            }
        }
        var confirmed = targets.Values.Sum(c => c.Confirmed); var failed = targets.Values.Sum(c => c.Failed);
        var missing = targets.Values.Count(c => c.Confirmed == 0 && c.Failed == 0);
        var success = missing == 0 && failed == 0 && unknown == 0;
        var detail = string.Join("；", targets.Select(t => $"{Path.GetFileName(t.Key)}：确认{t.Value.Confirmed}个实例" +
            (t.Value.Failed > 0 ? $"，{t.Value.Failed}个失败或身份/读回未知" : t.Value.Confirmed == 0 ? "，未发现可核验的运行实例" : "")));
        return (success, $"程序优先级{(success ? "已确认" : confirmed > 0 ? "部分完成" : "未完成")}：{detail}。" +
            (unknown > 0 ? $"另有{unknown}个同名候选身份无法核对，未修改，不能确认覆盖全部目标。" : "") +
            (denied > 0 ? $"{denied}个候选权限不足或系统拒绝访问。" : "") + "只处理本次捕获的实例，不重试、不恢复；取消或超时可能已修改。");

        void Fail(string path) { var count = targets[path]; targets[path] = (count.Confirmed, count.Failed + 1); }
    }
}
