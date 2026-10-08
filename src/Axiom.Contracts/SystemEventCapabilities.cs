using Axiom.Domain;

namespace Axiom.Contracts;

public static class SystemEventCapabilities
{
    public const string Event = "system.event";
    public static readonly string[] Kinds = ["locked", "unlocked", "resumed", "ac", "battery", "network-up", "network-down"];
    public static TaskCapability Descriptor { get; } = new(Event, 1, TaskCapabilityKind.Detection, "系统", "会话、电源或网络变化",
        "锁屏、解锁、睡眠恢复、供电或网络可用性变化后执行。",
        [new("event", "何时", TaskFieldKind.Choice, true, "仅响应新通知；保存或重连时不补发之前的事件。", "unlocked", 16,
            [new("locked", "锁屏时"), new("unlocked", "解锁时"), new("resumed", "从睡眠恢复时"), new("ac", "接通交流电时"), new("battery", "切换到电池时"), new("network-up", "网络可用时"), new("network-down", "网络不可用时")])],
        "使用 Windows 会话和电源通知，无周期检查。条件为一次事件；暂停、修改或删除取消尚未执行的延迟。",
        "睡眠恢复不等于用户已解锁。锁屏期间不适合键盘输入；电源事件以系统实际通知为准，未知供电不猜测。网络可用指本机存在可用网络接口，不代表互联网连通。");
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Event || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("event", out var kind) || !Kinds.Contains(kind, StringComparer.Ordinal)) throw new ArgumentException("系统事件配置无效。");
        return kind;
    }
}
