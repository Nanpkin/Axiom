using Axiom.Domain;

namespace Axiom.Contracts;

public static class BluetoothDeviceCapabilities
{
    public const string Connected = "bluetooth.device-connected";
    public const string Disconnected = "bluetooth.device-disconnected";
    private static readonly TaskField Names = new("deviceNames", "蓝牙设备名称", TaskFieldKind.TextList, true,
        "填写 Windows 蓝牙设置中已配对设备的完整名称，每行一个，最多 16 个；忽略大小写，不接受通配符。DualSense 常见名称为 Wireless Controller，请以本机名称为准。", null, 4096, [], EditAsList: true);
    public static TaskCapability ConnectedDescriptor { get; } = new(Connected, 1, TaskCapabilityKind.Detection, "设备", "指定蓝牙设备已连接",
        "任一指定名称的已配对蓝牙设备从未连接变为已连接时执行。", [Names],
        "订阅 Windows 蓝牙关联端点的连接状态变化，无周期扫描，不打开 HID 手柄。只读取蓝牙原设备，不把 DSX 模拟的有线手柄计入；HidHide 的输入访问隐藏不会作为断开依据。首次、重新启用、睡眠恢复或故障恢复只建立基线，不补发一次性动作。",
        "完整名称精确匹配；同名设备视为一组，仍有任一连接就保持成立。支持已配对的经典蓝牙及蓝牙 LE，不检测 USB 或未配对设备。查询缺失、通知故障或超出容量为未知，不当作断开。实际驱动兼容性需实机核对。", IsStateCondition: true);
    public static TaskCapability DisconnectedDescriptor { get; } = new(Disconnected, 1, TaskCapabilityKind.Detection, "设备", "指定蓝牙设备全部断开",
        "指定名称的已配对蓝牙设备从至少一只连接变为全部断开时执行。", [Names],
        "复用同一 Windows 蓝牙连接状态订阅；已配对但未连接不算在线，模拟的 USB 手柄不影响结果。首次、重新启用、睡眠恢复或故障恢复不补发一次性动作。",
        "只有全部匹配设备都能确认未连接才成立；同名设备无法按名称区分。未知状态不会触发隐藏；暂停任务不撤销已经执行的托盘操作。", IsStateCondition: true);

    public static bool ValidName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255 && !value.Any(c => char.IsControl(c) || c is '*' or '?');
    public static string[] Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new ArgumentException("请填写最多 16 个完整蓝牙设备名称。");
        var names = value.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length is < 1 or > 16 || names.Any(n => !ValidName(n))) throw new ArgumentException("蓝牙设备名称无效：每行一个完整名称，最多 16 个，不接受通配符或控制字符。");
        return names.Select(n => n.ToUpperInvariant()).ToArray();
    }
    public static string[] Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId is not (Connected or Disconnected) || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("deviceNames", out var names)) throw new ArgumentException("蓝牙设备检测参数无效。");
        return Parse(names);
    }
}
