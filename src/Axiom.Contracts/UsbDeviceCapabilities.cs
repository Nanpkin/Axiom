using Axiom.Domain;

namespace Axiom.Contracts;

public static class UsbDeviceCapabilities
{
    public const string Present = "usb.device-present";
    public const string Absent = "usb.device-absent";
    private static readonly TaskField Instances = new("deviceInstanceIds", "USB 设备实例路径", TaskFieldKind.TextList, true,
        "在设备管理器的物理 USB 设备属性→详细信息→设备实例路径复制完整 USB\\…\\… 值，每行一个，最多 16 个。不是硬件 ID（VID/PID）、设备接口路径或 HID/存储等子功能实例。", null, 4096, [], EditAsList: true);
    public static TaskCapability PresentDescriptor { get; } = new(Present, 1, TaskCapabilityKind.Detection, "设备", "指定 USB 设备已接入",
        "任一所选 USB 设备实例从不存在变为存在时执行。", [Instances],
        "按需共享 Windows USB 原生通知，合并已到达通知后查询当前 USB_DEVICE 接口对应的实例，无周期扫描。首次、重新启用或恢复基线不补发一次性动作；条件期间保持唤醒按已知当前状态生效。其他所选实例仍在时不重复触发。",
        "完整实例路径可能包含序列号，只保存在本地。仅匹配 USB_DEVICE 接口对应的实例，不推断父子设备或同型号设备；部分扩展坞有多个物理实例，需要分别复制。禁用、重枚举也可能改变存在状态，不证明人工插拔；短暂接入可能在查询前已结束。", IsStateCondition: true);
    public static TaskCapability AbsentDescriptor { get; } = new(Absent, 1, TaskCapabilityKind.Detection, "设备", "指定 USB 设备全部移除",
        "所选实例从至少一个存在变为全部不存在时执行。", [Instances],
        "复用同一 USB 通知和查询；全部所选实例都确认不存在才成立。无 USB 接口是已知空集合；首次或恢复时已不存在不补执行。查询故障或超出容量为未知，不当作已移除。",
        "此状态表示 Windows 当前没有报告匹配的 USB_DEVICE 接口实例，不保证设备已物理拔出。不会把 HID、USBSTOR 或设备接口路径自动换成物理 USB 实例；实例路径变化后需手工更新。", IsStateCondition: true);

    public static bool ValidInstanceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.Any(c => char.IsControl(c) || c is '*' or '?' or '/')) return false;
        var parts = value.Split('\\');
        return parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase) && parts[1].Length > 0 && parts[2].Length > 0;
    }
    public static string[] Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new ArgumentException("请填写最多 16 个完整 USB 设备实例路径。");
        var values = value.Split('\n').Select(s => s.EndsWith('\r') ? s[..^1] : s).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length is < 1 or > 16 || values.Any(s => !ValidInstanceId(s))) throw new ArgumentException("USB 设备实例路径无效：每行一个完整 USB\\设备\\实例 值，最多 16 个，不接受通配符或 VID/PID 子串。");
        return values.Select(s => s.ToUpperInvariant()).ToArray();
    }
    public static string[] Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId is not (Present or Absent) || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("deviceInstanceIds", out var ids)) throw new ArgumentException("USB 检测参数无效。");
        return Parse(ids);
    }
}
