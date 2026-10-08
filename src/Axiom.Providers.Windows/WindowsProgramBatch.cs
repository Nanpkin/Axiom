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
        if (results.Length == 1) return results[0];
        var completed = results.Count(r => r.Success);
        return new(completed == results.Length, $"已完成 {completed}/{results.Length} 个目标。\n" +
            string.Join("\n", results.Select((r, i) => $"{paths[i]} · {(r.Success ? "完成" : "未完成")}：{r.Reason}")));
    }
}
