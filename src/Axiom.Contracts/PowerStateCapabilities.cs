using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class PowerStateCapabilities
{
    public const string State = "power.state";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "电源", "供电与电量条件",
        "接电、电池供电，或电池供电且电量低于自选阈值时条件成立。",
        [new("mode", "供电条件", TaskFieldKind.Choice, true, "低电量条件仅在电池供电时成立；接电时结束。", "ac", 16,
            [new("ac", "已接电"), new("battery", "电池供电"), new("battery-below", "电池供电且低于阈值")]),
         new("threshold", "低电量阈值（%）", TaskFieldKind.Number, true, "仅低电量条件使用；严格低于此值才成立。", "20", 2, [], Minimum: 1, Maximum: 99)],
        "共享 Windows 供电和电量通知，通知后读取当前状态，无周期查询。首次、重新启用、睡眠恢复或未知状态恢复只建立基线，不补一次性动作；条件期间保持唤醒按已知当前状态生效。",
        "Windows 未知供电、电量或查询失败时，相关条件为未知，不当作低电量。无电池不会满足低电量条件。阈值变化前已发生的电量跨越不补执行；短暂变化可能在查询前已结束。");

    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("mode", out var mode) || mode is not ("ac" or "battery" or "battery-below") ||
            !invocation.Parameters.TryGetValue("threshold", out var threshold) || !int.TryParse(threshold, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 99)
            throw new ArgumentException("请选择供电条件，并填写1–99的整数电量阈值。");
        return mode == "battery-below" ? "battery-below:" + value.ToString(CultureInfo.InvariantCulture) : mode;
    }
    public static bool ValidResourceId(string id)
    {
        if (id is "ac" or "battery") return true;
        const string prefix = "battery-below:";
        return id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(id[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
            value is >= 1 and <= 99 && id == prefix + value.ToString(CultureInfo.InvariantCulture);
    }
}
