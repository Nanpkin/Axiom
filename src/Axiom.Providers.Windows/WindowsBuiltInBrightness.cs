using System.Management;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsBuiltInBrightness
{
    private static readonly BoundedNativeCommand Commands = new("屏幕亮度（自动选择）", TimeSpan.FromSeconds(10));
    public static Task DrainAsync() => Task.WhenAll(Commands.DrainAsync(), BrightnessNativeOwnership.DrainAsync()).WaitAsync(TimeSpan.FromSeconds(10));
    public static Task<(bool Success, string Reason)> SetAsync(int percent, string sid, CancellationToken token)
    {
        if (percent is < 0 or > 100) throw new ArgumentException("亮度须为0–100整数。");
        return RunSingleAsync(cancellation =>
        {
            using var api = new Api(cancellation);
            if (!api.Authorized(sid)) return (false, "当前用户或活动会话与亮度授权不一致，本次未修改。");
            return BuiltInBrightnessOperation.Apply(api, percent, cancellation, () => WindowsHardwareBrightness.SetOwned("primary", percent, sid, cancellation));
        }, token);
    }

    public static Task<(bool Success, string Reason)> AdjustAsync(string operation, int step, string sid, CancellationToken token)
    {
        if (operation is not ("increase" or "decrease") || step is < 1 or > 100) throw new ArgumentException("相对亮度参数无效。");
        return RunSingleAsync(cancellation =>
        {
            using var api = new Api(cancellation);
            if (!api.Authorized(sid)) return (false, "当前用户或活动会话与亮度授权不一致，本次未修改。");
            return BuiltInBrightnessOperation.Adjust(api, operation, step, cancellation, () => WindowsHardwareBrightness.AdjustOwned("primary", operation, step, sid, cancellation));
        }, token);
    }

    private sealed record Panel(ManagementObject Brightness, ManagementObject Methods, ManagementObject Connection);
    private static Task<(bool Success, string Reason)> RunSingleAsync(Func<CancellationToken, (bool Success, string Reason)> action, CancellationToken token) =>
        Commands.RunAsync(cancellation =>
        {
            using var ownership = BrightnessNativeOwnership.TryAcquire();
            return ownership is null ? (false, "Axiom已有亮度恢复租约或原生操作；请先暂停已有亮度恢复任务并等待释放完成，再执行其他亮度动作。本次未执行，不排队。") : action(cancellation);
        }, token);
    private sealed class Api(CancellationToken token) : IBuiltInBrightnessApi, IDisposable
    {
        internal bool Authorized(string sid) => WindowsHardwareBrightness.AuthorizedUser(sid, token);
        internal void UseToken(CancellationToken value) => token = value;
        private readonly List<ManagementObject> owned = [];
        private readonly Dictionary<string, Panel> supported = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<BrightnessPanel> FindPanels()
        {
            ManagementObject[] brightness;
            try { brightness = Query("SELECT Active,InstanceName,CurrentBrightness FROM WmiMonitorBrightness"); }
            catch (ManagementException error) when (owned.Count == 0 && (error.ErrorCode is ManagementStatus.NotSupported or ManagementStatus.InvalidClass)) { return []; }
            if (brightness.Length == 0) return [];
            var connections = Index(Query("SELECT Active,InstanceName,VideoOutputTechnology FROM WmiMonitorConnectionParams"));
            var duplicates = Index(brightness);
            var result = new List<BrightnessPanel>();
            var internalPanels = new List<(ManagementObject Brightness, string Key, ManagementObject Connection)>();
            foreach (var item in brightness)
            {
                token.ThrowIfCancellationRequested();
                var key = Key(item);
                if (item["Active"] is false) { result.Add(new(key ?? "", BrightnessPanelSupport.Ignore)); continue; }
                if (item["Active"] is not true || key is null || duplicates[key].Length != 1 ||
                    !connections.TryGetValue(key, out var linked) || linked.Length != 1 || linked[0]["Active"] is not true ||
                    linked[0]["VideoOutputTechnology"] is not uint technology)
                { result.Add(new(key ?? "", BrightnessPanelSupport.Unknown)); continue; }
                var connectionSupport = BuiltInBrightnessOperation.ConnectionSupport(technology);
                if (connectionSupport != BrightnessPanelSupport.Supported) { result.Add(new(key, connectionSupport)); continue; }
                internalPanels.Add((item, key, linked[0]));
            }
            if (internalPanels.Count == 0) return result;
            // A missing Methods class after finding an internal reader is a failure, not proof of no internal panel.
            var methods = Index(Query("SELECT Active,InstanceName FROM WmiMonitorBrightnessMethods"));
            foreach (var panel in internalPanels)
            {
                token.ThrowIfCancellationRequested();
                if (panel.Brightness["CurrentBrightness"] is not byte current || current > 100 || !methods.TryGetValue(panel.Key, out var writers) ||
                    writers.Length != 1 || writers[0]["Active"] is not true)
                { result.Add(new(panel.Key, BrightnessPanelSupport.Unsupported)); continue; }
                supported.Add(panel.Key, new(panel.Brightness, writers[0], panel.Connection)); result.Add(new(panel.Key, BrightnessPanelSupport.Supported));
            }
            return result;
        }
        public bool Verify(string key)
        {
            token.ThrowIfCancellationRequested();
            if (!supported.TryGetValue(key, out var panel)) return false;
            foreach (var item in new[] { panel.Brightness, panel.Methods, panel.Connection })
            {
                item.Get(); token.ThrowIfCancellationRequested();
                if (!string.Equals(Key(item), key, StringComparison.OrdinalIgnoreCase) || item["Active"] is not true) return false;
            }
            return panel.Connection["VideoOutputTechnology"] is uint technology && BuiltInBrightnessOperation.IsInternalConnection(technology) &&
                panel.Brightness["CurrentBrightness"] is byte current && current <= 100;
        }
        public uint Set(string key, byte brightness)
        {
            token.ThrowIfCancellationRequested();
            var writer = supported[key].Methods;
            using var parameters = writer.GetMethodParameters("WmiSetBrightness");
            token.ThrowIfCancellationRequested();
            parameters["Timeout"] = 0u; parameters["Brightness"] = brightness;
            // Do not ask the driver to schedule a later write. After admission cancellation, no new invoke is issued.
            token.ThrowIfCancellationRequested();
            using var result = writer.InvokeMethod("WmiSetBrightness", parameters, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(3) });
            token.ThrowIfCancellationRequested();
            return result?["ReturnValue"] is uint status ? status : 13u; // fail closed if the method result is missing
        }
        public int? Read(string key)
        {
            if (!Verify(key)) return null;
            return supported[key].Brightness["CurrentBrightness"] is byte value && value <= 100 ? value : null;
        }
        private ManagementObject[] Query(string query)
        {
            token.ThrowIfCancellationRequested();
            var scope = new ManagementScope(@"\\.\root\WMI", new ConnectionOptions { Timeout = TimeSpan.FromSeconds(3) });
            using var search = new ManagementObjectSearcher(scope, new ObjectQuery(query),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = false });
            using var collection = search.Get(); token.ThrowIfCancellationRequested();
            var result = new List<ManagementObject>();
            foreach (ManagementObject item in collection)
            {
                owned.Add(item); token.ThrowIfCancellationRequested();
                if (result.Count >= 128) throw new InvalidOperationException("WMI brightness object limit.");
                item.Options = new ObjectGetOptions { Timeout = TimeSpan.FromSeconds(3) }; result.Add(item);
            }
            return [.. result];
        }
        private static string? Key(ManagementObject item) => item["InstanceName"] is string { Length: > 0 and <= 2048 } key ? key : null;
        private static Dictionary<string, ManagementObject[]> Index(IEnumerable<ManagementObject> items) => items.Where(item => Key(item) is not null)
            .GroupBy(item => Key(item)!, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        public void Dispose()
        {
            var failed = false;
            foreach (var item in owned) try { item.Dispose(); } catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { failed = true; }
            if (failed) throw new IOException("部分内屏亮度资源释放未确认。");
        }
    }
}
