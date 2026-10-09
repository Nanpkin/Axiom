namespace Axiom.Providers.Windows;

public static partial class WindowsOpenTarget
{
    public static async Task<(bool Success, string Reason)> PrepareAsync((string Kind, string Target)[] steps, CancellationToken token)
    {
        if (steps is null || steps.Length is < 1 or > 8) throw new ArgumentException("工作准备须为1–8步。");
        var snapshot = steps.ToArray(); var progress = new WorkPreparationProgress(snapshot.Length);
        (bool Success, string Reason) result;
        try { result = await Commands.RunAsync(cancellation => WorkPreparationOperation.Apply(snapshot, new PreparationApi(), progress, cancellation), token); }
        catch (OperationCanceledException) { result = (false, "工作准备已取消，已停止后续步骤。"); }
        return (result.Success, result.Reason + " " + progress.Describe());
    }

    private sealed class PreparationApi : IWorkPreparationApi
    {
        private readonly BatchApi targets = new();
        public bool CanExecute(string kind, string target, CancellationToken token) => kind != "program"
            ? targets.CanOpen(kind, target, token)
            : targets.CanOpen("document", target, token) && targets.CanOpen("folder", Path.GetDirectoryName(target)!, token);
        public WorkPreparationStepResult Execute(string kind, string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (kind != "program")
            {
                var opened = WindowsOpenTarget.Open(kind, target, token);
                return new(opened.Success, Reason: opened.Reason);
            }
            // No nested timeout: the shared gate remains occupied until Start really returns.
            var result = WindowsProgramControl.Start(target, "", "", "normal", true, token);
            return new(result.Success, result.Skipped, result.Reason);
        }
    }
}
