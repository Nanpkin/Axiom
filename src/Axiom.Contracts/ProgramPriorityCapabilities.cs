using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class ProgramPriorityCapabilities
{
    public const string Set = "program.priority";
    public static TaskCapability Descriptor { get; } = new(Set, 1, TaskCapabilityKind.Action, "程序", "设置运行程序的优先级",
        "一次设置所选完整路径的当前运行实例，不启动程序。",
        [new("executable", "目标程序", TaskFieldKind.ExecutableSelection, true, "多选1–32个完整exe路径；只处理当前用户和会话。", null, ExecutablePaths.MaximumTextLength, []),
         new("priority", "优先级", TaskFieldKind.Choice, true, "仅调整CPU调度优先级，不限制CPU使用率。", "below-normal", 12,
            [new("below-normal", "低于正常"), new("normal", "正常"), new("above-normal", "高于正常")]),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未执行的动作。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "核对完整路径、用户、会话和实例身份，一次设置当前实例并读回。条件结束或暂停不恢复，后续新实例和手动调整不受干预。",
        "目标未运行、权限不足、身份或读回未知均会说明；多实例可能部分完成，取消或超时可能已修改。不自动重试或恢复。提高优先级可能影响其他程序响应；不提供高、实时或空闲优先级。");

    public static (string[] Paths, string Priority, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 3 ||
            !invocation.Parameters.TryGetValue("executable", out var executable) ||
            !invocation.Parameters.TryGetValue("priority", out var priority) || priority is not ("below-normal" or "normal" or "above-normal") ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择完整程序路径、支持的优先级和有效整数延迟。");
        return (ExecutablePaths.Parse(executable), priority, d);
    }
}
