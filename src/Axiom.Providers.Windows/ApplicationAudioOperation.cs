namespace Axiom.Providers.Windows;

internal enum AudioSessionIdentity { Owned, Other, Unknown }
internal interface IApplicationAudioSession : IAudioPlaybackEndpoint
{
    AudioSessionIdentity Identify(out string? path);
    bool IsActive { get; }
}

internal static class ApplicationAudioOperation
{
    internal static (bool Success, string Reason) Apply(IEnumerable<IApplicationAudioSession> sessions, IReadOnlyCollection<string> paths,
        string operation, int percent, CancellationToken token)
    {
        if (paths.Count == 0 || operation is not ("volume" or "mute" or "unmute") || percent is < 0 or > 100) throw new ArgumentException("应用音量配置无效。");
        var targets = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, _ => (Succeeded: 0, Failed: 0), StringComparer.OrdinalIgnoreCase);
        var unknown = 0;
        foreach (var session in sessions)
        {
            token.ThrowIfCancellationRequested();
            string? path = null;
            try
            {
                // Inactive/expired sessions are outside this one-shot action; stale PIDs there must not create false identity gaps.
                if (!session.IsActive) continue;
                var identity = session.Identify(out path);
                if (identity == AudioSessionIdentity.Unknown) { unknown++; continue; }
                if (identity != AudioSessionIdentity.Owned || path is null || !targets.ContainsKey(path)) continue;
                // Recheck immediately before the write. Native adapters pin the process handle until session disposal.
                if (session.Identify(out var verified) != AudioSessionIdentity.Owned || !string.Equals(path, verified, StringComparison.OrdinalIgnoreCase) || !session.IsActive)
                { var count = targets[path]; targets[path] = (count.Succeeded, count.Failed + 1); continue; }
                var result = AudioPlaybackOperation.Apply(session, operation, percent, token, "应用音频会话");
                var previous = targets[path]; targets[path] = result.Success ? (previous.Succeeded + 1, previous.Failed) : (previous.Succeeded, previous.Failed + 1);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                if (path is not null && targets.TryGetValue(path, out var previous)) targets[path] = (previous.Succeeded, previous.Failed + 1);
                else unknown++;
            }
        }
        var succeeded = targets.Values.Sum(v => v.Succeeded); var failed = targets.Values.Sum(v => v.Failed);
        var missing = targets.Values.Count(v => v.Succeeded == 0 && v.Failed == 0);
        var success = missing == 0 && failed == 0 && unknown == 0;
        var detail = string.Join("；", targets.Select(t => $"{Path.GetFileName(t.Key)}：确认{t.Value.Succeeded}个会话" +
            (t.Value.Failed > 0 ? $"，{t.Value.Failed}个失败或身份变化" : t.Value.Succeeded == 0 ? "，未播放或无匹配活动会话" : "")));
        return (success, $"应用音量{(success ? "已确认" : succeeded > 0 ? "部分完成" : "未完成")}：{detail}。" +
            (unknown > 0 ? $"另有{unknown}个会话身份或状态无法核对，未修改这些会话，不能确认覆盖全部目标。" : "") +
            "只处理执行开始时默认播放设备的活动会话；不重试、不恢复旧值。");
    }
}
