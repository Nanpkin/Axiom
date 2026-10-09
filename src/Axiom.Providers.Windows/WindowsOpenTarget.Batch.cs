namespace Axiom.Providers.Windows;

public static partial class WindowsOpenTarget
{
    // Uses the existing 8-second gate for the entire batch, not a fresh timeout per target.
    public static async Task<(bool Success, string Reason)> OpenBatchAsync((string Kind, string Target)[] targets, CancellationToken token)
    {
        if (targets is null || targets.Length is < 1 or > 8) throw new ArgumentException("工作目标数量须为1–8项。");
        var snapshot = targets.ToArray(); var progress = new OpenTargetBatchProgress(snapshot.Length);
        (bool Success, string Reason) result;
        try { result = await Commands.RunAsync(cancellation => OpenTargetBatchOperation.Apply(snapshot, new BatchApi(), progress, cancellation), token); }
        catch (OperationCanceledException) { result = (false, "工作目标打开已取消，已停止后续项。"); }
        return (result.Success, result.Reason + " " + progress.Describe());
    }

    private sealed class BatchApi : IOpenTargetBatchApi
    {
        public bool CanOpen(string kind, string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (kind == "url") return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;
            if (kind is not ("folder" or "document") || !Path.IsPathFullyQualified(target) || target.StartsWith("\\\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal)) return false;
            try
            {
                if ((kind == "folder" ? !Directory.Exists(target) : !File.Exists(target)) || new DriveInfo(Path.GetPathRoot(target)!).DriveType == DriveType.Network) return false;
                token.ThrowIfCancellationRequested(); return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
        public bool Open(string kind, string target, CancellationToken token) => WindowsOpenTarget.Open(kind, target, token).Success;
    }
}
