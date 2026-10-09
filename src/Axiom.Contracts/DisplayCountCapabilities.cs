using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class DisplayCountCapabilities
{
    public const string Count = "display.count";
    public static TaskCapability Descriptor { get; } = new(Count, 1, TaskCapabilityKind.Detection, "显示", "显示器数量条件",
        "根据Windows当前逻辑桌面显示器数量执行，或在条件成立期间保持唤醒。",
        [new("comparison", "数量条件", TaskFieldKind.Choice, true, "退回单屏可选恰好1个；多屏工作可选至少2个。", "at-least", 8,
            [new("at-least", "至少"), new("exact", "恰好")]),
         new("count", "显示器数量", TaskFieldKind.Number, true, "1–16；统计当前Windows逻辑桌面，不读取设备身份或布局坐标。", "2", 2, [], Minimum: 1, Maximum: 16)],
        "共享显示布局与恢复通知，无定时轮询。持续动作可用首次已知状态；一次性动作只响应后续条件变化，不补发首次连接、恢复或未知后的既有状态。暂停和删除释放订阅。",
        "镜像显示、远程桌面和虚拟显示器可能与物理屏幕数量不同；不识别具体显示器。查询失败、空布局、超过64个逻辑显示器或通知丢失时状态未知，不当作单屏。布局在动作前仍可能变化，动作自行核验。");
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Count || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("comparison", out var comparison) || comparison is not ("at-least" or "exact") ||
            !invocation.Parameters.TryGetValue("count", out var text) || text.Length is < 1 or > 2 ||
            !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 16)
            throw new ArgumentException("请填写至少或恰好，以及1–16的整数显示器数量。");
        return comparison + ":" + count.ToString(CultureInfo.InvariantCulture);
    }
    public static bool ValidResourceId(string resource)
    {
        var separator = resource.IndexOf(':');
        return separator > 0 && resource[..separator] is "at-least" or "exact" &&
            int.TryParse(resource[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= 16 &&
            resource[(separator + 1)..] == count.ToString(CultureInfo.InvariantCulture);
    }
}
