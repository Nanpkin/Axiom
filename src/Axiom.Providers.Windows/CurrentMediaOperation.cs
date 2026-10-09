namespace Axiom.Providers.Windows;

internal enum CurrentMediaPlaybackStatus { Unknown, Paused, Playing, Other }
internal readonly record struct CurrentMediaPlayback(CurrentMediaPlaybackStatus Status, bool CanPause, bool CanPlay, bool CanPrevious, bool CanNext);
internal interface ICurrentMediaSession
{
    CurrentMediaPlayback ReadPlayback();
    bool Request(string operation);
}
internal interface ICurrentMediaApi : IDisposable
{
    ICurrentMediaSession? CaptureCurrent();
    bool IsCurrent(ICurrentMediaSession captured);
}

internal static class CurrentMediaOperation
{
    internal static (bool Success, string Reason) Apply(ICurrentMediaApi api, string operation, CancellationToken token)
    {
        if (operation is not ("pause" or "play" or "previous" or "next")) throw new ArgumentException("Unsupported media operation.", nameof(operation));
        token.ThrowIfCancellationRequested();
        var session = api.CaptureCurrent();
        token.ThrowIfCancellationRequested();
        if (session is null) return (false, "Windows没有选中的当前媒体会话，本次未发送请求；请先在支持Windows媒体控制的播放器中打开媒体。");
        var playback = session.ReadPlayback();
        token.ThrowIfCancellationRequested();
        if (!api.IsCurrent(session)) return (false, "Windows当前媒体会话已切换或消失，本次未发送请求；不会改控其他会话，不自动重试。");
        token.ThrowIfCancellationRequested();
        if (operation == "pause" && playback.Status == CurrentMediaPlaybackStatus.Paused)
            return (true, "Windows当前媒体会话报告已经暂停，本次无需操作，未发送重复请求。");
        if (operation == "play" && playback.Status == CurrentMediaPlaybackStatus.Playing)
            return (true, "Windows当前媒体会话报告已经播放，本次无需操作，未发送重复请求。");
        var (enabled, name) = operation switch
        {
            "pause" => (playback.CanPause, "暂停"), "play" => (playback.CanPlay, "播放"),
            "previous" => (playback.CanPrevious, "上一项"), _ => (playback.CanNext, "下一项")
        };
        if (!enabled) return (false, $"Windows当前媒体会话未启用{name}操作，本次未发送请求；支持范围由播放器决定。");
        token.ThrowIfCancellationRequested();
        var accepted = session.Request(operation);
        token.ThrowIfCancellationRequested();
        return accepted
            ? (true, $"Windows当前媒体会话已接受{name}请求，实际播放或媒体切换是否完成未确认；不自动重试、不恢复。")
            : (false, $"Windows当前媒体会话未接受{name}请求，结果未确认；不自动重试、不恢复。");
    }
}
