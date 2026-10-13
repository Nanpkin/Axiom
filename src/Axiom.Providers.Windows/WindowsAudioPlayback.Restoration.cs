using System.Globalization;
using System.Runtime.InteropServices;

namespace Axiom.Providers.Windows;

public static partial class WindowsAudioPlayback
{
    public static async Task<IRestorableSettingSession> CreateRestorableSessionAsync(bool microphone, bool volume, CancellationToken token)
    {
        if (microphone && volume) throw new ArgumentException("麦克风恢复只支持静音。");
        AudioSession? session = null;
        (bool Success, string Reason) result;
        try { result = await Commands.RunAsync(cancellation =>
        {
            IDeviceEnumerator? enumerator = null; IDevice? device = null; object? activated = null; nint mta = 0;
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
            try
            {
                // Keep the COM MTA alive between bounded native workers; this lease owns the cookie.
                Marshal.ThrowExceptionForHR(CoIncrementMTAUsage(out mta));
                cancellation.ThrowIfCancellationRequested();
                var cls = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"); var iid = typeof(IDeviceEnumerator).GUID;
                Marshal.ThrowExceptionForHR(CoCreateInstance(ref cls, 0, 1, ref iid, out enumerator));
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(microphone ? 1 : 0, microphone ? 2 : 0, out device));
                var id = ReadId(device); iid = typeof(IEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out activated));
                session = new(enumerator, device, activated, id, microphone, volume, mta);
                enumerator = null; device = null; activated = null; mta = 0;
                cancellation.ThrowIfCancellationRequested();
                return (true, "已绑定本轮默认端点并读取原设置。");
            }
            finally
            {
                if (activated is not null) Marshal.ReleaseComObject(activated);
                if (device is not null) Marshal.ReleaseComObject(device);
                if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
                if (mta != 0) Marshal.ThrowExceptionForHR(CoDecrementMTAUsage(mta));
                CoUninitialize();
            }
        }, token); }
        catch
        {
            await Commands.DrainAsync();
            if (session is not null) await session.DisposeAsync();
            throw;
        }
        if (result.Success && session is not null) return session;
        await Commands.DrainAsync();
        if (session is not null) await session.DisposeAsync();
        throw new IOException(result.Reason);
    }
    private static string ReadId(IDevice device)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var pointer));
        try { return pointer == 0 ? throw new IOException("无法核验音频端点身份。") : Marshal.PtrToStringUni(pointer) ?? throw new IOException("音频端点身份为空。"); }
        finally { if (pointer != 0) Marshal.FreeCoTaskMem(pointer); }
    }
    private sealed class AudioSession : SerializedRestorableSettingSession
    {
        private readonly IDeviceEnumerator enumerator;
        private readonly IDevice device;
        private readonly object activated;
        private readonly string id;
        private readonly bool microphone, volume;
        private readonly nint mta;
        private readonly Endpoint endpoint;
        private readonly ConditionalSettingTransaction<float>? level;
        private readonly ConditionalSettingTransaction<bool>? mute;
        public AudioSession(IDeviceEnumerator enumerator, IDevice device, object activated, string id, bool microphone, bool volume, nint mta) : base(Commands)
        {
            this.enumerator = enumerator; this.device = device; this.activated = activated; this.id = id; this.microphone = microphone; this.volume = volume;
            this.mta = mta;
            endpoint = new((IEndpointVolume)activated);
            if (volume) level = new(ReadVolume(), ReadVolume, endpoint.SetVolume, "默认播放音量",
                acceptsTarget: (actual, target) => Math.Abs(actual - target) <= 0.005f);
            else mute = new(ReadMute(), ReadMute, endpoint.SetMuted, microphone ? "默认通信麦克风静音" : "默认播放静音");
        }
        private void Verify()
        {
            IDevice? current = null;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(microphone ? 1 : 0, microphone ? 2 : 0, out current));
                if (ReadId(current) != id) throw new IOException("本轮默认端点已改变，不操作新端点。");
                Marshal.ThrowExceptionForHR(device.GetState(out var state));
                if (state != 1) throw new IOException("本轮端点已失效。");
            }
            finally { if (current is not null) Marshal.ReleaseComObject(current); }
        }
        private float ReadVolume()
        {
            Verify(); var value = endpoint.GetVolume();
            return float.IsFinite(value) && value is >= 0 and <= 1 ? value : throw new IOException("音量基线未知。");
        }
        private bool ReadMute() { Verify(); return endpoint.GetMuted(); }
        private static RestorableSettingResult OnMta(Func<RestorableSettingResult> action)
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
            try { return action(); } finally { CoUninitialize(); }
        }
        protected override RestorableSettingResult ApplyCore(IReadOnlyDictionary<string, string> parameters, CancellationToken token) => OnMta(() =>
        {
            var operation = parameters["operation"];
            if (volume != (operation == "volume")) { RelinquishCore(); return new(false, "本轮设置字段变化，未改原目标或新字段。", false); }
            return volume ? level!.Apply(int.Parse(parameters["percent"], CultureInfo.InvariantCulture) / 100f, token)
                : mute!.Apply(operation == "mute", token);
        });
        protected override RestorableSettingResult ReleaseCore(CancellationToken token) => OnMta(() => volume ? level!.Release(token) : mute!.Release(token));
        protected override void RelinquishCore() { level?.Relinquish(); mute?.Relinquish(); }
        protected override void DisposeCore()
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
            try
            {
                try { Marshal.ReleaseComObject(activated); }
                finally { try { Marshal.ReleaseComObject(device); } finally { Marshal.ReleaseComObject(enumerator); } }
            }
            finally { try { Marshal.ThrowExceptionForHR(CoDecrementMTAUsage(mta)); } finally { CoUninitialize(); } }
        }
    }
    [DllImport("ole32.dll")] private static extern int CoIncrementMTAUsage(out nint cookie);
    [DllImport("ole32.dll")] private static extern int CoDecrementMTAUsage(nint cookie);
}
