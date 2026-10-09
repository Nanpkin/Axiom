using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class PowerSchemeCapabilities
{
    public const string Set = "power.scheme";
    public static Guid Balanced { get; } = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static Guid Saver { get; } = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static Guid Performance { get; } = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static TaskCapability Descriptor { get; } = new(Set, 1, TaskCapabilityKind.Action, "电源", "切换Windows电源方案",
        "一次切换到本机已有的标准平衡、节能或高性能方案。",
        [new("scheme", "电源方案", TaskFieldKind.Choice, true, "仅使用本机已存在的标准方案；缺失时失败，不创建或导入。", "balanced", 16,
            [new("balanced", "平衡"), new("saver", "节能"), new("performance", "高性能")]),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未执行的动作。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "先确认所选标准方案存在，切换一次并读回当前方案；之后保留新方案，可在Windows设置中手动更改。暂停、删除或检测条件结束不会恢复旧方案，不持续控制、不自动重试。",
        "影响整台电脑，可能改变耗电、性能和休眠策略。Modern Standby或厂商设备可能只提供平衡方案；方案缺失或系统拒绝切换会明确失败。此动作不创建方案、不修改方案内部设置，也不切换Windows设置中的电源模式滑块。取消或超时可能已经切换，结果未确认时不重试或恢复。");

    public static (Guid Scheme, string Name, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("scheme", out var scheme) || scheme is not ("balanced" or "saver" or "performance") ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择标准电源方案，并填写0–86400的整数延迟。");
        return scheme switch { "balanced" => (Balanced, "平衡", d), "saver" => (Saver, "节能", d), _ => (Performance, "高性能", d) };
    }
}
