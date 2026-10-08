using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsAudioPlayback
{
    private static readonly BoundedNativeCommand Commands = new("音频", TimeSpan.FromSeconds(5));
    public static Task<(bool Success, string Reason)> SetAsync(string operation, int percent, CancellationToken token)
    {
        if (operation is not ("volume" or "mute" or "unmute") || percent is < 0 or > 100) throw new ArgumentException("音量操作无效。");
        return Commands.RunAsync(cancellation => Apply(operation, percent, false, cancellation), token);
    }
    public static Task<(bool Success, string Reason)> SetMicrophoneAsync(string operation, CancellationToken token)
    {
        if (operation is not ("mute" or "unmute")) throw new ArgumentException("麦克风静音操作无效。");
        return Commands.RunAsync(cancellation => Apply(operation, 0, true, cancellation), token);
    }
    private static (bool Success, string Reason) Apply(string operation, int percent, bool microphone, CancellationToken token)
    {
        IDeviceEnumerator? enumerator = null; IDevice? device = null; object? activated = null; var initialized = false;
        try
        {
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0)); initialized = true;
            var cls = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"); var iid = typeof(IDeviceEnumerator).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref cls, 0, 1, ref iid, out enumerator));
            token.ThrowIfCancellationRequested();
            // Bind once: playback eRender/eConsole, microphone eCapture/eCommunications; no device IDs or enumeration.
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(microphone ? 1 : 0, microphone ? 2 : 0, out device));
            token.ThrowIfCancellationRequested(); iid = typeof(IEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out activated));
            return AudioPlaybackOperation.Apply(new Endpoint((IEndpointVolume)activated), operation, percent, token, microphone ? "默认通信麦克风" : "默认播放设备");
        }
        finally
        {
            if (activated is not null) Marshal.ReleaseComObject(activated);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            if (initialized) CoUninitialize();
        }
    }
    private sealed class Endpoint(IEndpointVolume volume) : IAudioPlaybackEndpoint
    {
        private Guid context = new("0DE0208A-126F-4C92-9CA8-4C0AD69ADBFC");
        public void SetVolume(float value) => Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(value, ref context));
        public float GetVolume() { Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var value)); return value; }
        public void SetMuted(bool value) => Marshal.ThrowExceptionForHR(volume.SetMute(value, ref context));
        public bool GetMuted() { Marshal.ThrowExceptionForHR(volume.GetMute(out var value)); return value; }
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, [MarshalAs(UnmanagedType.Interface)] out IDevice device);
        [PreserveSig] int GetDevice(nint id, out nint device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint mode, out nint properties);
        [PreserveSig] int GetId(out nint id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint channels);
        [PreserveSig] int SetMasterVolumeLevel(float decibels, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float scalar, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float decibels);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float scalar);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float scalar, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float scalar);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid cls, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IDeviceEnumerator instance);
}
