using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class WindowMonitorCapabilities
{
    public const string Move = "window.monitor";
    public static TaskCapability Descriptor { get; } = new(Move, 1, TaskCapabilityKind.Action, "窗口", "把窗口移到显示器",
        "将所选程序的一个可见主窗口移到主显示器、下一个或上一个显示器，并摆到目标工作区中央（80%）。",
        [.. WindowCapabilities.Descriptor.Fields.Select(f => f.Id == "title" ? f with { Hint = "留空匹配可见主窗口；填写可避免移动同一程序的其他主窗口。" } : f), new("monitor", "目标显示器", TaskFieldKind.Choice, true,
            "执行时按显示器桌面横坐标、纵坐标排序并循环切换；不保存设备身份。只有一屏时上/下一屏跳过。", "primary", 16,
            [new("primary", "主显示器"), new("next", "下一个显示器"), new("previous", "上一个显示器")])],
        "多程序独立等待，每个目标只处理一个可见主窗口。最大化或最小化窗口先还原，再居中；仅一屏的上/下一屏不会改动窗口。",
        "一次操作并读回；程序限制窗口大小、拓扑变化或手动调整可能令结果未确认。不会持续固定窗口，也不恢复之前的大小、位置或最大化状态。");
    public static string Value(TaskInvocation invocation, string field) => invocation.Parameters.GetValueOrDefault(field)
        ?? Descriptor.Fields.Single(f => f.Id == field).DefaultValue ?? "";
    public static decimal Number(TaskInvocation invocation, string field) => decimal.Parse(Value(invocation, field), CultureInfo.InvariantCulture);
    public static string[] ValidateTargets(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Move || invocation.SchemaVersion != 1 || invocation.Parameters is null ||
            invocation.Parameters.Keys.Any(k => !Descriptor.Fields.Any(f => f.Id == k))) throw new ArgumentException("窗口移屏参数无效。");
        foreach (var field in Descriptor.Fields)
        {
            var value = Value(invocation, field.Id);
            if (field.Required && string.IsNullOrWhiteSpace(value) || value.Length > field.MaximumLength) throw new ArgumentException("请正确填写" + field.Label + "。");
            if (field.Kind == TaskFieldKind.Number && (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) || n < field.Minimum || n > field.Maximum))
                throw new ArgumentException(field.Label + "超出范围。");
            if (field.Kind == TaskFieldKind.Choice && !field.Choices.Any(c => c.Value == value)) throw new ArgumentException(field.Label + "选项无效。");
        }
        return ExecutablePaths.Parse(Value(invocation, "executable"));
    }
}
