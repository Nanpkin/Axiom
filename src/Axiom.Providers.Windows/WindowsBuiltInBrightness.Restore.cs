using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsBuiltInBrightness
{
    public static Task<IRestorableSettingSession> CreateRestorableSessionAsync(string sid, CancellationToken token) =>
        BrightnessRestoreSession.CreateAsync(cancellation => Capture(sid, cancellation), null, TimeSpan.FromSeconds(10), token);
    private static BrightnessRestoreCapture Capture(string sid, CancellationToken token)
    {
        var topology = new BrightnessRestoreTopology(); var api = new Api(token); var handedOff = false;
        try
        {
            if (!api.Authorized(sid)) throw new IOException("当前用户与亮度授权不一致。");
            var panels = api.FindPanels(); token.ThrowIfCancellationRequested();
            if (panels.Count > 128) throw new IOException("内屏亮度目标过多。");
            var duplicates = panels.Where(p => p.Support == BrightnessPanelSupport.Supported).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Where(g => string.IsNullOrEmpty(g.Key) || g.Count() != 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets = new List<BrightnessRestoreTarget>(); var unknown = 0; var unsupported = 0;
            foreach (var panel in panels)
            {
                token.ThrowIfCancellationRequested();
                if (panel.Support == BrightnessPanelSupport.Ignore) continue;
                if (panel.Support == BrightnessPanelSupport.Unsupported) { unsupported++; continue; }
                if (panel.Support != BrightnessPanelSupport.Supported || duplicates.Contains(panel.Key)) { unknown++; continue; }
                try
                {
                    var baseline = api.Read(panel.Key); token.ThrowIfCancellationRequested();
                    if (!topology.Valid || baseline is not (>= 0 and <= 100)) { unknown++; continue; }
                    targets.Add(new((uint)baseline.Value, p => p is >= 0 and <= 100 ? (uint)p : null,
                        () => topology.Valid && api.Authorized(sid) && api.Read(panel.Key) is { } current && current is >= 0 and <= 100 ? (uint)current : null,
                        value => topology.Valid && api.Authorized(sid) && api.Verify(panel.Key) && api.Set(panel.Key, (byte)value) == 0));
                }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException)) { unknown++; }
            }
            var capture = new BrightnessRestoreCapture(targets, unknown, unsupported, api.UseToken,
                () => { try { api.Dispose(); } finally { topology.Dispose(); } });
            handedOff = true; return capture;
        }
        finally { if (!handedOff) { try { api.Dispose(); } finally { topology.Dispose(); } } }
    }
}
