using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsUsbDevices
{
    private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    public static async Task ObserveAsync(string[] instances, Action<UsbDeviceValue[], bool> publish, CancellationToken token)
    {
        // The shared window registers this exact interface class before we enumerate its present instances.
        using var events = new WindowsSessionEvents(usbOnly: true);
        await UsbDeviceObservation.RunAsync(instances, events.Events, () => ReadPresentInstances(token), publish, token, () => events.Failed);
    }
    public static string[] ReadPresentInstances(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var classGuid = UsbDeviceInterface;
        var devices = SetupDiGetClassDevs(ref classGuid, null, 0, 0x12); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (devices == -1 || devices == 0) throw Error();
        try
        {
            var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devices, 0, ref classGuid, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == 259) return [.. instances]; // ERROR_NO_MORE_ITEMS: known empty is valid
                    throw Error();
                }
                if (index >= 256 || (data.Flags & 4) != 0) throw new IOException("USB 接口数量超出 256 或枚举中发生移除，状态未知。");
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (SetupDiGetDeviceInterfaceDetail(devices, ref data, 0, 0, out var size, ref info) || Marshal.GetLastWin32Error() != 122 || size is < 8 or > 4096)
                    throw new IOException("USB 接口详情大小或查询状态无效。");
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    // The Unicode detail cbSize is 8 on x64 and 6 on x86. Its returned symbolic link is never read.
                    Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
                    info.Size = (uint)Marshal.SizeOf<DeviceInfo>();
                    if (!SetupDiGetDeviceInterfaceDetail(devices, ref data, detail, size, out _, ref info)) throw Error();
                    var instance = new StringBuilder(256);
                    if (!SetupDiGetDeviceInstanceId(devices, ref info, instance, 256, out var required) || required is < 2 or > 256) throw Error();
                    var id = instance.ToString();
                    if (id.Length is < 1 or > 255 || id.Any(char.IsControl)) throw new IOException("USB 实例查询返回无效身份。");
                    instances.Add(id); // physical PnP instance, not the interface path or VID/PID category
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
    }
    private static Win32Exception Error() => new(Marshal.GetLastWin32Error(), "无法核验当前 USB 设备实例。");
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public uint Size; public Guid ClassGuid; public uint Flags; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint Instance; public nint Reserved; }
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(nint devices, nint info, ref Guid classGuid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetail(nint devices, ref InterfaceData data, nint detail, uint size, out uint required, ref DeviceInfo info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceId(nint devices, ref DeviceInfo info, StringBuilder instance, uint size, out uint required);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint devices);
}
