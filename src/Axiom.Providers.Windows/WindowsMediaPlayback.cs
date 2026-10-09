using System.Runtime.Versioning;
using System.Threading.Channels;
using Windows.Media.Control;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsMediaPlayback : IMediaPlaybackSource
{
    private static readonly object Gate = new();
    private static MediaPlaybackHub? shared;
    private readonly IMediaPlaybackSource lease;
    public WindowsMediaPlayback()
    {
        lock (Gate)
        {
            if (shared?.Stopped.Task.IsCompleted == true) shared = null;
            shared ??= new(() => new Api(GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult()));
            lease = shared.Subscribe();
        }
    }
    public ChannelReader<MediaPlaybackSample> Events => lease.Events;
    public bool Failed => lease.Failed;
    public void Dispose() => lease.Dispose();
    private sealed class Api : IMediaPlaybackApi
    {
        private readonly GlobalSystemMediaTransportControlsSessionManager manager;
        public event Action? CurrentChanged;
        internal Api(GlobalSystemMediaTransportControlsSessionManager manager) { this.manager = manager; manager.CurrentSessionChanged += Changed; }
        private void Changed(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => CurrentChanged?.Invoke();
        public IMediaPlaybackSession? Current() => manager.GetCurrentSession() is { } current ? new Session(current) : null;
        public void Dispose() => manager.CurrentSessionChanged -= Changed;
    }
    private sealed class Session(GlobalSystemMediaTransportControlsSession native) : IMediaPlaybackSession
    {
        private Action? changed;
        public event Action PlaybackChanged
        {
            add { if (changed is null) native.PlaybackInfoChanged += Changed; changed += value; }
            remove { changed -= value; if (changed is null) native.PlaybackInfoChanged -= Changed; }
        }
        private void Changed(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => changed?.Invoke();
        public bool Same(IMediaPlaybackSession other) => other is Session session && WindowsCurrentMedia.SameNativeSession(native, session.Native);
        private GlobalSystemMediaTransportControlsSession Native => native;
        public MediaPlaybackStatus Read() => native.GetPlaybackInfo().PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackStatus.Stopped,
            _ => MediaPlaybackStatus.Unknown
        };
        public void Dispose() { if (changed is not null) { native.PlaybackInfoChanged -= Changed; changed = null; } }
    }
}
