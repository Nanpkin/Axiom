using System.ComponentModel;

namespace Axiom.Providers.Windows;

public static class WindowsProgramBatch
{
    public static async Task<ProgramControlResult> ExecuteAsync(IReadOnlyList<string> paths,
        Func<string, CancellationToken, Task<ProgramControlResult>> execute, CancellationToken token)
    {
        if (paths.Count is < 1 or > 32) throw new ArgumentException("请选择 1–32 个目标程序。");
        token.ThrowIfCancellationRequested();
        async Task<ProgramControlResult> Run(string path)
        {
            try { token.ThrowIfCancellationRequested(); return await execute(path, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
            { return new(false, error.Message); }
        }
        // All targets start their bounded wait together, staying within the command deadline.
        var results = await Task.WhenAll(paths.Select(Run));
        token.ThrowIfCancellationRequested();
        var skipped = results.Count(r => r.Success && r.Skipped);
        var completed = results.Count(r => r.Success && !r.Skipped);
        var failed = results.Count(r => !r.Success);
        var summary = skipped == results.Length ? $"无需处理 {skipped} 个目标。" :
            $"完成 {completed} 个" + (skipped > 0 ? $"，无需处理 {skipped} 个" : "") + (failed > 0 ? $"，失败 {failed} 个" : "") + "。";
        static string State(ProgramControlResult result) => !result.Success ? "失败" : result.Skipped ? "无需处理" : "完成";
        // Preserve every target and its status before potentially long explanations.
        var targets = string.Join("\n", results.Select((result, index) => $"{index + 1}. 【{State(result)}】{paths[index]}"));
        var groups = results.Select((result, index) => (Result: result, Index: index + 1))
            .GroupBy(item => (State: State(item.Result), item.Result.Reason))
            .Select(group => "【" + group.Key.State + "】" + group.Key.Reason + "\n对应第 " + string.Join("、", group.Select(item => item.Index)) + " 项");
        return new(failed == 0, summary + "\n\n" + targets + "\n\n" + string.Join("\n\n", groups)) { Skipped = skipped == results.Length };
    }
}
