using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsHardwareBrightness
{
    public static Task<IRestorableSettingSession> CreateRestorableSessionAsync(string target, string sid, CancellationToken token)
    {
        if (target is not ("primary" or "all")) throw new ArgumentException("亮度恢复屏幕范围无效。");
        return BrightnessRestoreSession.CreateAsync(cancellation => Capture(target, sid, cancellation), target, TimeSpan.FromSeconds(10), token);
    }
    internal static bool AuthorizedUser(string sid, CancellationToken token) => new Api(sid, token).IsAuthorized();
    internal static BrightnessRestoreCapture Capture(string selection, string sid, CancellationToken token)
    {
        var topology = new BrightnessRestoreTopology(); var api = new Api(sid, token);
        var leases = new List<IHardwareMonitorLease>(); var handedOff = false;
        try
        {
            if (!api.IsAuthorized()) throw new IOException("当前用户或活动会话与亮度授权不一致。");
            var logical = api.ReadLogicalMonitors(); token.ThrowIfCancellationRequested();
            if (logical.Count is < 1 or > 64 || logical.Any(m => m.Handle == 0) || logical.Select(m => m.Handle).Distinct().Count() != logical.Count || logical.Count(m => m.Primary) != 1)
                throw new IOException("无法确认完整显示布局及主屏。");
            var handles = new List<nint>(); var unknown = 0; var unsupported = 0; var count = 0;
            foreach (var monitor in logical.Where(m => selection == "all" || m.Primary))
            {
                try
                {
                    var lease = api.Acquire(monitor.Handle, 64 - count); leases.Add(lease); token.ThrowIfCancellationRequested();
                    count += lease.Handles.Count;
                    if (count > 64 || lease.Handles.Count > 8) throw new HardwareBrightnessLimitException();
                    if (lease.Handles.Count == 0 || lease.Handles.Any(h => h == 0) || lease.Handles.Distinct().Count() != lease.Handles.Count) { unknown++; continue; }
                    if (lease.Handles.Any(handles.Contains)) throw new InvalidDataException("物理句柄重复，目标未知。");
                    handles.AddRange(lease.Handles);
                }
                catch (Exception error) when (error is HardwareBrightnessLimitException or InvalidDataException) { throw; }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException)) { unknown++; }
            }
            var targets = new List<BrightnessRestoreTarget>();
            foreach (var monitor in handles)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var target = HardwareBrightnessOperation.CaptureTarget(api, monitor, () => topology.Valid, token, out var notSupported);
                    if (target is null) { if (notSupported) unsupported++; else unknown++; continue; }
                    targets.Add(target);
                }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException)) { unknown++; }
            }
            var capture = new BrightnessRestoreCapture(targets, unknown, unsupported, api.UseToken, () => CloseResources(leases, topology));
            handedOff = true; return capture;
        }
        finally { if (!handedOff) CloseResources(leases, topology); }
    }
    private static void CloseResources(IEnumerable<IHardwareMonitorLease> leases, BrightnessRestoreTopology topology)
    {
        var failed = false;
        foreach (var lease in leases) try { lease.Dispose(); } catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { failed = true; }
        try { topology.Dispose(); } finally { if (failed) throw new IOException("部分物理亮度句柄释放未确认。"); }
    }
}
