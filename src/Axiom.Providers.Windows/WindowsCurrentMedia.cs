using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Media.Control;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows10.0.17763.0")]
public static class WindowsCurrentMedia
{
    private static readonly BoundedNativeCommand Commands = new("Windows当前媒体控制", TimeSpan.FromSeconds(5));
    public static Task DrainAsync() => Commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    public static Task<(bool Success, string Reason)> ControlAsync(string operation, CancellationToken token) =>
        Commands.RunAsync(cancellation =>
        {
            try
            {
                // Wait for the original WinRT operation, without a cancellation wrapper. Admission
                // remains held until it actually completes, even when our caller times out/cancels.
                using var api = OpenApiAsync().GetAwaiter().GetResult();
                return CurrentMediaOperation.Apply(api, operation, cancellation);
            }
            catch (Exception error) when (error is UnauthorizedAccessException || error is COMException { HResult: unchecked((int)0x80070005) })
            { return (false, "无法访问Windows当前媒体控制：权限不足或系统策略拒绝；本次结果未确认，不自动重试。"); }
        }, token);

    // Also used by Windows CI to exercise the real, unpackaged desktop API without
    // sending any playback commands or collecting media properties/application IDs.
    internal static async Task<ICurrentMediaApi> OpenApiAsync() => new Api(await GlobalSystemMediaTransportControlsSessionManager.RequestAsync());

    private sealed class Api : ICurrentMediaApi
    {
        private readonly GlobalSystemMediaTransportControlsSessionManager manager;
        private int generation;
        internal Api(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            this.manager = manager;
            manager.CurrentSessionChanged += OnCurrentChanged;
        }
        private void OnCurrentChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Interlocked.Increment(ref generation);
        public ICurrentMediaSession? CaptureCurrent()
        {
            var version = Volatile.Read(ref generation);
            var current = manager.GetCurrentSession();
            return current is null ? null : new Session(this, current, version);
        }
        public bool IsCurrent(ICurrentMediaSession captured)
        {
            if (captured is not Session session || !ReferenceEquals(session.Owner, this) || session.Generation != Volatile.Read(ref generation)) return false;
            var current = manager.GetCurrentSession();
            return current is not null && SameNativeSession(session.Native, current) && session.Generation == Volatile.Read(ref generation);
        }
        public void Dispose() => manager.CurrentSessionChanged -= OnCurrentChanged;
    }

    private sealed class Session(Api owner, GlobalSystemMediaTransportControlsSession native, int generation) : ICurrentMediaSession
    {
        internal Api Owner => owner;
        internal GlobalSystemMediaTransportControlsSession Native => native;
        internal int Generation => generation;
        public CurrentMediaPlayback ReadPlayback()
        {
            var playback = native.GetPlaybackInfo(); var controls = playback.Controls;
            var status = playback.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => CurrentMediaPlaybackStatus.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => CurrentMediaPlaybackStatus.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => CurrentMediaPlaybackStatus.Other,
                _ => CurrentMediaPlaybackStatus.Unknown
            };
            return new(status, controls.IsPauseEnabled, controls.IsPlayEnabled, controls.IsPreviousEnabled, controls.IsNextEnabled);
        }
        public bool Request(string operation) => (operation switch
        {
            "pause" => native.TryPauseAsync(), "play" => native.TryPlayAsync(),
            "previous" => native.TrySkipPreviousAsync(), "next" => native.TrySkipNextAsync(),
            _ => throw new ArgumentException("Unsupported media operation.", nameof(operation))
        }).AsTask().GetAwaiter().GetResult();
    }

    internal static bool SameNativeSession(GlobalSystemMediaTransportControlsSession left, GlobalSystemMediaTransportControlsSession right)
    {
        // Different projected wrappers can represent the same COM object. Compare canonical
        // IUnknown identities; QueryInterface owns an AddRef, so release both in all paths.
        var unknown = new Guid("00000000-0000-0000-C000-000000000046");
        nint leftIdentity = 0, rightIdentity = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(((WinRT.IWinRTObject)left).NativeObject.ThisPtr, in unknown, out leftIdentity));
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(((WinRT.IWinRTObject)right).NativeObject.ThisPtr, in unknown, out rightIdentity));
            return leftIdentity != 0 && leftIdentity == rightIdentity;
        }
        finally
        {
            if (rightIdentity != 0) Marshal.Release(rightIdentity);
            if (leftIdentity != 0) Marshal.Release(leftIdentity);
            GC.KeepAlive(left); GC.KeepAlive(right);
        }
    }
}
