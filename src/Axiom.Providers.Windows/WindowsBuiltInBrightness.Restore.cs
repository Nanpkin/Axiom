using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsBuiltInBrightness
{
    public static Task<IRestorableSettingSession> CreateRestorableSessionAsync(string sid, CancellationToken token) =>
        BrightnessRestoreSession.CreateAsync(cancellation => Capture(sid, cancellation), null, TimeSpan.FromSeconds(10), token);
    private static BrightnessRestoreCapture Capture(string sid, CancellationToken token)
    {
        var topology = new BrightnessRestoreTopology(); var api = new Api(token);
        return BuiltInBrightnessOperation.Capture(api, () => api.Authorized(sid), () => topology.Valid, api.UseToken,
            () => { try { api.Dispose(); } finally { topology.Dispose(); } },
            () => WindowsHardwareBrightness.Capture("primary", sid, token), token);
    }
}
