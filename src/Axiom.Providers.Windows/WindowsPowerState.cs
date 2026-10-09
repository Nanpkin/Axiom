using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsPowerState
{
    public static async Task ObserveAsync(string[] resources, Action<PowerStateValue[], bool> publish, CancellationToken token)
    {
        using var events = new WindowsSessionEvents(powerOnly: true);
        await PowerStateObservation.RunAsync(resources, events.Events, () => Read(token), publish, token, () => events.Failed);
    }
    public static PowerStateSnapshot Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!GetSystemPowerStatus(out var status)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前供电与电量状态。");
        token.ThrowIfCancellationRequested();
        return new(status.AcLine, status.BatteryFlag, status.BatteryPercent);
    }
    public static byte ReadSaverStatus(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!GetSystemPowerStatus(out var status)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取Windows节能状态报告。");
        token.ThrowIfCancellationRequested();
        return status.SystemStatus; // SYSTEM_POWER_STATUS.SystemStatusFlag; only documented 0/1 are Known
    }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte AcLine, BatteryFlag, BatteryPercent, SystemStatus; public uint LifeTime, FullLifeTime; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
