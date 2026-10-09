using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationAudioActivity : IApplicationAudioActivitySource
{
    private static readonly object Gate = new();
    private static ApplicationAudioActivityHub? shared;
    private readonly IApplicationAudioActivitySource lease;
    public WindowsApplicationAudioActivity(string[] paths)
    {
        if (paths.Length is < 1 or > 128) throw new ArgumentException("应用播放检测资源数量无效。");
        lock (Gate)
        {
            if (shared is null || shared.Stopped.Task.IsCompleted) shared = new(WindowsAudioPlayback.CreateActivityApi);
            lease = shared.Subscribe(paths);
        }
    }
    public System.Threading.Channels.ChannelReader<ApplicationAudioActivitySample> Events => lease.Events;
    public bool Failed => lease.Failed;
    public void Dispose() => lease.Dispose();
}
