using Axiom.Domain;

namespace Axiom.Contracts;

public static class InternetConnectivityCapabilities
{
    public const string State = "network.internet-state";
    public const string Resource = "windows-report";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "网络", "Windows 报告的 Internet 状态",
        "按Windows报告的Internet可用或不可用状态执行，不只判断是否存在网络接口。",
        [new("state", "Internet 条件", TaskFieldKind.Choice, true, "选择Windows报告可用或不可用；查询失败不当作不可用。", "available", 12,
            [new("available", "Windows 报告 Internet 可用"), new("unavailable", "Windows 报告 Internet 不可用")])],
        "按需共享Windows Network List Manager原生通知，通知后只读当前IPv4/IPv6 Internet标志，无周期查询，不发ping或HTTP探测。首次、重新启用及未知后恢复只建立基线，不补一次性动作；已知持续条件可保持唤醒。",
        "这是Windows连通性报告，不保证任意网站、同步服务或应用可达；VPN、受限网络、策略或禁用NCSI可能影响报告。系统明确仅本地网络或已断开才可判不可用，服务、订阅或查询失败为未知。不读取网络名称、SSID、地址、位置或设备标识，不判断网络可信性。", IsStateCondition: true);
    public static bool Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("state", out var state) || state is not ("available" or "unavailable"))
            throw new ArgumentException("请选择Windows报告Internet可用或不可用。");
        return state == "available";
    }
}
