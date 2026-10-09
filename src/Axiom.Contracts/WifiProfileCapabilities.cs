using Axiom.Domain;

namespace Axiom.Contracts;

public static class WifiProfileCapabilities
{
    public const string Connected = "wifi.profile-connected";
    public const string Disconnected = "wifi.profile-disconnected";
    private static readonly TaskField Profiles = new("profileNames", "Wi-Fi 配置文件名", TaskFieldKind.TextList, true,
        "每行一个本机保存的 Wi-Fi 配置文件名，最多 16 个；名称区分大小写。配置文件名可能与网络显示名称不同，可在 Windows 终端用 netsh wlan show profiles 查看。", null, 4096, [], EditAsList: true);
    public static TaskCapability ConnectedDescriptor { get; } = new(Connected, 1, TaskCapabilityKind.Detection, "网络", "指定 Wi-Fi 已连接",
        "任一所选 Wi-Fi 配置文件从未连接变为已连接时执行。", [Profiles],
        "按需共享 Windows 原生 Wi-Fi 通知，无周期检查。首次、重新启用或重连时已连接不补执行；同组选项之间切换不重复触发。",
        "只核验本机 Wi-Fi 配置文件，不检查互联网连通性，不记录或导出 SSID、地址或密码。极短连接可能在查询前已结束；配置文件名只保存在本地。无 Wi-Fi 网卡、服务不可用或 Windows 隐私设置阻止查询时为未知。");
    public static TaskCapability DisconnectedDescriptor { get; } = new(Disconnected, 1, TaskCapabilityKind.Detection, "网络", "指定 Wi-Fi 全部断开",
        "所选 Wi-Fi 配置文件从至少一个已连接变为全部未连接时执行。", [Profiles],
        "复用同一原生通知；只有所有网卡都能确认未连接到所选配置文件才成立。首次、重新启用或重连时已断开不补执行。",
        "未知网卡状态不当作断开。无 Wi-Fi 硬件、服务不可用或查询失败为未知；不检查有线网络或互联网连通性。");

    public static string[] Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new ArgumentException("请填写最多 16 个 Wi-Fi 配置文件名。");
        var names = value.Split('\n').Select(s => s.EndsWith('\r') ? s[..^1] : s).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length is < 1 or > 16 || names.Any(n => !ValidName(n))) throw new ArgumentException("Wi-Fi 配置文件名无效：每行一个，最多 16 个，每个最多 255 个字符，不含控制字符。");
        return names;
    }
    public static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 255 && !name.Any(char.IsControl);
}
