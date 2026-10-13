namespace Axiom.Providers.Windows;

internal sealed record HardwareLogicalMonitor(nint Handle, bool Primary);
internal sealed record HardwareBrightnessRange(uint Minimum, uint Current, uint Maximum);
internal interface IHardwareMonitorLease : IDisposable
{
    IReadOnlyList<nint> Handles { get; }
}
internal interface IHardwareBrightnessApi
{
    bool IsAuthorized();
    IReadOnlyList<HardwareLogicalMonitor> ReadLogicalMonitors();
    IHardwareMonitorLease Acquire(nint logicalMonitor, int remainingCapacity);
    uint? Capabilities(nint monitor);
    HardwareBrightnessRange? Read(nint monitor);
    bool Set(nint monitor, uint brightness);
}

internal sealed class HardwareBrightnessLimitException : Exception { }

internal static class HardwareBrightnessOperation
{
    internal static uint? Map(HardwareBrightnessRange? range, int percent)
    {
        if (percent is < 0 or > 100 || range is null || range.Maximum <= range.Minimum || range.Current < range.Minimum || range.Current > range.Maximum) return null;
        // DWORD ranges can span almost UInt32.MaxValue; use UInt64 before multiplying or rounding.
        return (uint)(range.Minimum + ((ulong)range.Maximum - range.Minimum) * (uint)percent / 100);
    }
    internal static uint? AdjustedValue(HardwareBrightnessRange? range, int delta)
    {
        if (delta is < -100 or > 100 or 0 || Map(range, 0) is null) return null;
        var distance = (((ulong)range!.Maximum - range.Minimum) * (uint)Math.Abs(delta) + 50) / 100;
        return delta > 0 ? (uint)Math.Min((ulong)range.Maximum, range.Current + distance) :
            (uint)Math.Max((ulong)range.Minimum, (ulong)range.Current - Math.Min((ulong)range.Current, distance));
    }
    internal static (bool Success, string Reason) Apply(IHardwareBrightnessApi api, string target, int percent, CancellationToken token) => ApplyCore(api, target, percent, null, token);
    internal static (bool Success, string Reason) Adjust(IHardwareBrightnessApi api, string target, string operation, int step, CancellationToken token)
    {
        if (operation is not ("increase" or "decrease") || step is < 1 or > 100) throw new ArgumentException("相对亮度参数无效。");
        return ApplyCore(api, target, 0, operation == "increase" ? step : -step, token);
    }
    private static (bool Success, string Reason) ApplyCore(IHardwareBrightnessApi api, string target, int percent, int? delta, CancellationToken token)
    {
        if (target is not ("primary" or "all") || percent is < 0 or > 100) throw new ArgumentException("硬件亮度参数无效。");
        token.ThrowIfCancellationRequested();
        IReadOnlyList<HardwareLogicalMonitor> logical;
        try
        {
            var authorized = api.IsAuthorized(); token.ThrowIfCancellationRequested();
            if (!authorized) return (false, "当前用户或活动会话与授权不一致，本次未修改屏幕亮度。");
            logical = api.ReadLogicalMonitors(); token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        { return (false, "无法确认当前会话或完整显示布局，本次未修改屏幕亮度。"); }
        if (logical.Count is < 1 or > 64 || logical.Any(m => m.Handle == 0) || logical.Select(m => m.Handle).Distinct().Count() != logical.Count ||
            logical.Count(m => m.Primary) != 1)
            return (false, "当前显示布局为空、超出范围或主屏无法确认，本次未修改屏幕亮度。");
        var leases = new List<IHardwareMonitorLease>(); var handles = new List<nint>();
        var unknown = 0; var unsupported = 0; var failed = 0; var succeeded = 0; var cleanupFailed = 0; var ownedCount = 0;
        try
        {
            foreach (var monitor in logical.Where(m => target == "all" || m.Primary))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var lease = api.Acquire(monitor.Handle, 64 - ownedCount); leases.Add(lease); // own before checking cancellation after native acquisition
                    token.ThrowIfCancellationRequested();
                    ownedCount += lease.Handles.Count;
                    if (ownedCount > 64) return (false, "物理屏幕总数超出64块，本次未修改；已取得的句柄将释放。");
                    if (lease.Handles.Count is < 1 or > 8 || lease.Handles.Any(h => h == 0) || lease.Handles.Distinct().Count() != lease.Handles.Count)
                    { unknown++; continue; }
                    if (lease.Handles.Any(handles.Contains)) return (false, "物理屏幕句柄存在重复，目标无法确认，本次未修改。");
                    handles.AddRange(lease.Handles);
                }
                catch (HardwareBrightnessLimitException) { return (false, "物理屏幕数量超出每个逻辑显示8块或总计64块的范围，本次未修改；已取得的句柄将释放。"); }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException)) { unknown++; }
            }
            for (var index = 0; index < handles.Count; index++)
            {
                var monitor = handles[index];
                token.ThrowIfCancellationRequested();
                uint requested;
                HardwareBrightnessRange range;
                try
                {
                    var caps = api.Capabilities(monitor); token.ThrowIfCancellationRequested();
                    if (!caps.HasValue) { unknown++; continue; }
                    if ((caps.Value & 0x2) == 0) { unsupported++; continue; } // MC_CAPS_BRIGHTNESS
                    var read = api.Read(monitor); token.ThrowIfCancellationRequested();
                    var mapped = delta.HasValue ? AdjustedValue(read, delta.Value) : Map(read, percent);
                    if (!mapped.HasValue) { unknown++; continue; }
                    range = read!; requested = mapped.Value;
                    var authorized = api.IsAuthorized(); token.ThrowIfCancellationRequested();
                    if (!authorized) { unknown += handles.Count - index; break; }
                }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException)) { unknown++; continue; }
                try
                {
                    token.ThrowIfCancellationRequested();
                    var accepted = api.Set(monitor, requested); token.ThrowIfCancellationRequested();
                    if (!accepted) { failed++; continue; }
                    var actual = api.Read(monitor); token.ThrowIfCancellationRequested();
                    if (actual is not null && actual.Minimum == range.Minimum && actual.Maximum == range.Maximum && actual.Current == requested) succeeded++;
                    else failed++;
                }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException)) { failed++; }
            }
        }
        finally
        {
            // Always close every acquired lease, including failed/partial discovery and canceled or blocked native calls.
            foreach (var lease in leases)
                try { lease.Dispose(); } catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { cleanupFailed++; }
        }
        var success = succeeded > 0 && unknown == 0 && failed == 0 && cleanupFailed == 0;
        return (success, $"硬件屏幕亮度{(success ? "已确认" : succeeded > 0 ? "部分完成" : "未确认")}：已确认{succeeded}块{(delta.HasValue ? $"相对调整{delta.Value}个百分点（范围限幅）" : $"为{percent}%")}，已知不支持而跳过{unsupported}块，状态或范围未知而未写入{unknown}个目标，写入/读回未确认{failed}块" +
            (cleanupFailed == 0 ? "。" : $"，{cleanupFailed}组句柄释放未确认。") + "新亮度保留，不重试、不恢复旧值；需要屏幕与连接支持且启用硬件亮度控制。");
    }
}
