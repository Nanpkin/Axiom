using Microsoft.Win32;

namespace Axiom.ServiceHost;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsBootIdentity
{
    // Documented boot counter; read once per Host start, never inferred from wall time.
    public static uint Read()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
        if (key?.GetValue("BootId") is not int value || key.GetValueKind("BootId") != RegistryValueKind.DWord)
            throw new InvalidDataException("无法读取 Windows 开机标识，首次启动任务未执行。");
        return unchecked((uint)value);
    }
}
