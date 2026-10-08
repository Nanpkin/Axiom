using System.Collections.Immutable;
using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class WindowCapabilities
{
    public const string Close = "window.close";
    public static TaskCapability Descriptor { get; } = new(Close, 1, TaskCapabilityKind.Action, "窗口", "关闭窗口",
        "模拟点击目标程序主窗口的 ×。支持关闭到托盘的软件会继续运行；其他软件可能退出。",
        [new("executable", "目标程序", TaskFieldKind.ExecutableSelection, true, "可批量添加最多 32 个完整 .exe 路径，匹配当前用户会话；不强制结束进程。", null, ExecutablePaths.MaximumTextLength, []),
         new("waitSeconds", "等待窗口出现（秒）", TaskFieldKind.Number, true, "窗口已显示时立即处理；否则等待窗口事件，超时会说明未找到窗口。", "30", 8, [], 1, 30),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "0 表示条件成立后开始等待。暂停、修改或删除会取消尚未执行的动作。", "0", 8, [], 0, 86400),
         new("title", "窗口标题包含", TaskFieldKind.Text, false, "留空匹配可见主窗口；填写可避免关闭同一程序的其他主窗口。", "", 256, [], Group: "更多选项")],
        "每次触发为每个目标关闭一个可见主窗口，独立等待并报告结果；不持续关闭后来手动打开的窗口。",
        "关闭效果与点击 × 相同，由目标程序决定是否留在托盘。不强制结束进程，不处理确认对话框。");
    public static string Value(TaskInvocation config, string field) => config.Parameters.GetValueOrDefault(field)
        ?? Descriptor.Fields.Single(f => f.Id == field).DefaultValue ?? "";
    public static decimal Number(TaskInvocation config, string field) => decimal.Parse(Value(config, field), CultureInfo.InvariantCulture);
    public static string Validate(TaskInvocation config) => ValidateTargets(config).Single();
    public static string[] ValidateTargets(TaskInvocation config)
    {
        if (config.CapabilityId != Close || config.SchemaVersion != 1 || config.Parameters is null || config.Parameters.Keys.Any(k => !Descriptor.Fields.Any(f => f.Id == k)))
            throw new ArgumentException("窗口操作配置无效。");
        foreach (var field in Descriptor.Fields)
        {
            var value = Value(config, field.Id);
            if (field.Required && string.IsNullOrWhiteSpace(value) || value.Length > field.MaximumLength) throw new ArgumentException("请正确填写" + field.Label + "。");
            if (field.Kind == TaskFieldKind.Number && (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) || n < field.Minimum || n > field.Maximum))
                throw new ArgumentException(field.Label + "超出范围。");
        }
        return ExecutablePaths.Parse(Value(config, "executable"));
    }
}
