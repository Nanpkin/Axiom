
namespace Axiom.Providers.Windows;

internal enum BrightnessPanelSupport { Supported, Ignore, Unsupported, Unknown }
internal sealed record BrightnessPanel(string Key, BrightnessPanelSupport Support);
internal interface IBuiltInBrightnessApi
{
    IReadOnlyList<BrightnessPanel> FindPanels();
    bool Verify(string key);
    uint Set(string key, byte brightness);
    int? Read(string key);
}

internal static class BuiltInBrightnessOperation
{
    internal static bool IsInternalConnection(uint technology) => technology is 0x80000000 or 6 or 11 or 13; // INTERNAL/LVDS/embedded DisplayPort/embedded UDI
    internal static BrightnessPanelSupport ConnectionSupport(uint technology) => IsInternalConnection(technology) ? BrightnessPanelSupport.Supported :
        technology is <= 5 or 8 or 9 or 10 or 12 or 14 or 15 or 16 or 17 ? BrightnessPanelSupport.Ignore : BrightnessPanelSupport.Unknown;
    internal static bool UsePrimaryHardware(IReadOnlyList<BrightnessPanel> panels) => panels.All(p => p.Support == BrightnessPanelSupport.Ignore);
    internal static (bool Success, string Reason) Apply(IBuiltInBrightnessApi api, int percent, CancellationToken token,
        Func<(bool Success, string Reason)>? primaryHardware = null, BrightnessFadeOptions fade = default,
        TimeProvider? time = null, Action<TimeSpan, CancellationToken>? wait = null) => ApplyCore(api, percent, null, token, primaryHardware, fade, time, wait);
    internal static (bool Success, string Reason) Adjust(IBuiltInBrightnessApi api, string operation, int step, CancellationToken token,
        Func<(bool Success, string Reason)>? primaryHardware = null, BrightnessFadeOptions fade = default,
        TimeProvider? time = null, Action<TimeSpan, CancellationToken>? wait = null)
    {
        if (operation is not ("increase" or "decrease") || step is < 1 or > 100) throw new ArgumentException("相对亮度参数无效。");
        return ApplyCore(api, 0, operation == "increase" ? step : -step, token, primaryHardware, fade, time, wait);
    }
    private static (bool Success, string Reason) ApplyCore(IBuiltInBrightnessApi api, int percent, int? delta, CancellationToken token,
        Func<(bool Success, string Reason)>? primaryHardware, BrightnessFadeOptions fade,
        TimeProvider? time, Action<TimeSpan, CancellationToken>? wait)
    {
        if (percent is < 0 or > 100) throw new ArgumentException("亮度须为0–100整数。");
        token.ThrowIfCancellationRequested();
        IReadOnlyList<BrightnessPanel> panels;
        try { panels = api.FindPanels(); }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        { return (false, "Windows WMI亮度接口不可用，本次未修改；可能没有支持的内置面板，或服务、驱动和权限不允许读取。"); }
        token.ThrowIfCancellationRequested();
        if (panels.Count > 128) return (false, "亮度对象数量超出可安全处理范围，本次未修改。");
        if (primaryHardware is not null && UsePrimaryHardware(panels))
        {
            token.ThrowIfCancellationRequested();
            var result = primaryHardware();
            return (result.Success, "没有活动内置亮度接口，已自动选择主屏硬件亮度。" + result.Reason);
        }
        var ambiguous = panels.Where(p => p.Support == BrightnessPanelSupport.Supported).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => string.IsNullOrEmpty(g.Key) || g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = panels.Count(p => p.Support is BrightnessPanelSupport.Unknown or BrightnessPanelSupport.Unsupported || ambiguous.Contains(p.Key));
        var succeeded = 0; var failed = 0;
        var transitions = new List<(BrightnessRestoreTarget Target, uint Value)>();
        foreach (var panel in panels.Where(p => p.Support == BrightnessPanelSupport.Supported && !ambiguous.Contains(p.Key)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var verified = api.Verify(panel.Key); token.ThrowIfCancellationRequested();
                if (!verified) { failed++; continue; }
                var requested = percent;
                if (delta.HasValue || fade.Enabled)
                {
                    var current = api.Read(panel.Key); token.ThrowIfCancellationRequested();
                    if (current is not (>= 0 and <= 100)) { failed++; continue; }
                    requested = delta.HasValue ? Math.Clamp(current.Value + delta.Value, 0, 100) : percent;
                    if (fade.Enabled)
                    {
                        var captured = new BrightnessRestoreTarget((uint)current.Value, p => (uint)p,
                            () => api.Read(panel.Key) is >= 0 and <= 100 and var value ? (uint)value : null,
                            value => api.Verify(panel.Key) && api.Set(panel.Key, (byte)value) == 0);
                        transitions.Add((captured, (uint)requested)); continue;
                    }
                }
                var error = api.Set(panel.Key, (byte)requested); token.ThrowIfCancellationRequested();
                if (error != 0) { failed++; continue; }
                var actual = api.Read(panel.Key); token.ThrowIfCancellationRequested();
                if (actual == requested) succeeded++; else failed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { failed++; }
        }
        if (fade.Enabled)
        {
            BrightnessFade.Apply(transitions, fade, token, time, wait);
            succeeded += transitions.Count(t => t.Target.Transaction.Owned);
            failed += transitions.Count(t => !t.Target.Transaction.Owned);
        }
        if (succeeded == 0 && failed == 0)
            return (false, "没有可确认支持WMI亮度设置的活动内置面板，本次未修改；外接显示器不在范围，缺失接口或未知身份不会尝试设置。");
        var success = failed == 0 && skipped == 0;
        return (success, $"内置屏幕亮度{(success ? "已确认" : succeeded > 0 ? "部分完成" : "未确认")}：{succeeded}块面板读回确认{(delta.HasValue ? $"相对调整{delta.Value}个百分点（0–100%限幅）" : $"为{percent}%")}，{failed}块写入/读回失败或身份变化，{skipped}块接口缺失或身份无法核对而未修改。新亮度保留，不重试、不恢复旧值；不处理外接显示器。");
    }
    internal static BrightnessRestoreCapture Capture(IBuiltInBrightnessApi api, Func<bool> authorized, Func<bool> valid,
        Action<CancellationToken> useToken, Action close, Func<BrightnessRestoreCapture> primaryHardware, CancellationToken token)
    {
        var handedOff = false; var closed = false;
        void Close() { if (closed) return; closed = true; close(); }
        try
        {
            if (!authorized()) throw new IOException("当前用户与亮度授权不一致。");
            var panels = api.FindPanels(); token.ThrowIfCancellationRequested();
            if (panels.Count > 128) throw new IOException("内屏亮度目标过多。");
            if (UsePrimaryHardware(panels)) { Close(); token.ThrowIfCancellationRequested(); return primaryHardware(); }
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
                    if (!valid() || baseline is not (>= 0 and <= 100)) { unknown++; continue; }
                    targets.Add(new((uint)baseline.Value, p => p is >= 0 and <= 100 ? (uint)p : null,
                        () => valid() && authorized() && api.Read(panel.Key) is { } current && current is >= 0 and <= 100 ? (uint)current : null,
                        value => valid() && authorized() && api.Verify(panel.Key) && api.Set(panel.Key, (byte)value) == 0));
                }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException)) { unknown++; }
            }
            var capture = new BrightnessRestoreCapture(targets, unknown, unsupported, useToken, Close);
            handedOff = true; return capture;
        }
        finally { if (!handedOff) Close(); }
    }
}
