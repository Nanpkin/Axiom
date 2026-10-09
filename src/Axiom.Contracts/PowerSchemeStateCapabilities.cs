using Axiom.Domain;

namespace Axiom.Contracts;

public static class PowerSchemeStateCapabilities
{
    public const string State = "power.scheme-state";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "电源", "当前Windows电源方案",
        "当前使用所选标准平衡、节能或高性能电源方案时条件成立。",
        [new("scheme", "标准电源方案", TaskFieldKind.Choice, true, "按标准方案GUID匹配；自定义方案不满足任何标准项。", "balanced", 16,
            [new("balanced", "平衡"), new("saver", "节能"), new("performance", "高性能")])],
        "共享Windows电源方案变化通知后读取当前方案，无周期查询。首次、重新启用、睡眠恢复或未知恢复只建立基线，不补一次性动作；已知成立期间的保持唤醒可直接生效。退出界面后后台继续检测。",
        "这是电源方案，不是Windows设置中的电源模式滑块。有效自定义方案为已知不匹配；查询或通知异常为未知。短暂方案切换可能在查询前结束。不修改或创建电源方案，也不能组合切换方案动作。");
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("scheme", out var scheme) || !ValidResourceId(scheme))
            throw new ArgumentException("请选择标准平衡、节能或高性能电源方案。");
        return scheme;
    }
    public static bool ValidResourceId(string id) => id is "balanced" or "saver" or "performance";
}
