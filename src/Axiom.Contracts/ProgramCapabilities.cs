using System.Collections.Immutable;
using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

// Shared declaration and validation. OS effects belong to the session Provider.
public static class ProgramCapabilities
{
    public static TaskCapability Describe(bool start) => new(start ? "program.start" : "program.close", 1,
        TaskCapabilityKind.Action, "程序", start ? "启动程序" : "关闭程序",
        start ? "条件成立时启动指定程序一次。" : "条件成立时关闭当前用户会话中指定的程序。",
        start ? [Executable, Delay,
            new("arguments", "启动参数", TaskFieldKind.Text, false, "直接传给程序，例如 --profile work；不执行命令脚本。", "", 4096, [], Group: "更多选项"),
            new("workingDirectory", "工作目录", TaskFieldKind.DirectoryPath, false, "留空使用程序所在目录。", "", 1024, [], Group: "更多选项"),
            new("window", "窗口状态", TaskFieldKind.Choice, true, "普通隐藏由目标程序决定是否接受。启动遮蔽按桌面用户的正常权限启动目标并加载保护组件；窗口自行隐藏或保护结束时，先隐藏再解除遮蔽，之后手动打开不再拦截。仅支持 x64 桌面程序，不用于游戏、受保护或必须管理员运行的程序。", "normal", 16,
                [new("normal", "正常显示"), new("minimized", "最小化"), new("hidden", "隐藏"), new("startup-hidden", "启动遮蔽（实验性）")], Group: "更多选项"),
            new("startupProtectionSeconds", "启动保护最长时间（秒）", TaskFieldKind.Number, false, "仅用于启动遮蔽，1–20 整数秒，默认 15。再次启动同路径程序时尝试提前解除保护；动作完成后手动打开不再拦截。极快退出的启动器可能无法确认，保护期内请稍候。晚于保护期的弹窗仍可能显示，安全软件可能阻止保护组件。", "15", 2, [], 1, 20, "更多选项"),
            new("ifRunning", "程序已运行时", TaskFieldKind.Choice, true, "按完整路径识别当前用户的程序实例。", "skip", 16,
                [new("skip", "跳过启动"), new("new", "再开一个实例")], Group: "更多选项")]
        : [new("executable", "目标程序", TaskFieldKind.ExecutableSelection, true,
            "可批量选择或输入最多 32 个本机程序的完整 .exe 路径；关闭选项应用于每个目标。", null, ExecutablePaths.MaximumTextLength, []), Delay,
            new("mode", "关闭方式", TaskFieldKind.Choice, true, "正常关闭会请求窗口退出；强制结束可能丢失未保存内容。", "graceful", 16,
                [new("graceful", "正常关闭"), new("force", "强制结束")]),
            new("timeoutSeconds", "等待退出（秒）", TaskFieldKind.Number, true, "超时会报告失败，不自动强制结束。", "10", 8, [], 1, 30, "更多选项"),
            new("instances", "关闭范围", TaskFieldKind.Choice, true, "只影响当前用户、当前会话且路径完全匹配的实例。", "all", 16,
                [new("all", "所有匹配实例"), new("latest", "最近启动的一个")], Group: "更多选项")],
        "条件由不成立变为成立时执行一次；条件必须持续满足所设延迟。启动时已有的状态不会补执行。",
        "修改或暂停会取消尚未执行的动作；已启动的程序不会自动关闭，已关闭的程序不会自动重开。");

    private static TaskField Executable => new("executable", "目标程序", TaskFieldKind.FilePath, true,
        "选择本机 .exe 文件；不接受快捷方式、脚本或网络路径。", null, 1024, []);
    private static TaskField Delay => new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true,
        "0 表示立即执行。等待期间条件结束、任务暂停或删除会取消。", "0", 8, [], 0, 86400);
    public static string Value(TaskInvocation config, string id) => config.Parameters.GetValueOrDefault(id)
        ?? Describe(config.CapabilityId == "program.start").Fields.Single(f => f.Id == id).DefaultValue ?? "";
    public static decimal Number(TaskInvocation config, string id) => decimal.Parse(Value(config, id), CultureInfo.InvariantCulture);
    public static string Validate(TaskInvocation config) => ValidateTargets(config).Single();
    public static string[] ValidateTargets(TaskInvocation config)
    {
        if (config.CapabilityId is not ("program.start" or "program.close") || config.SchemaVersion != 1 || config.Parameters is null)
            throw new ArgumentException("程序能力配置版本无效。");
        var descriptor = Describe(config.CapabilityId == "program.start");
        if (config.Parameters.Keys.Any(k => !descriptor.Fields.Any(f => f.Id == k))) throw new ArgumentException("包含未知程序参数。");
        foreach (var field in descriptor.Fields)
        {
            var value = Value(config, field.Id);
            if (field.Required && string.IsNullOrWhiteSpace(value) || value.Length > field.MaximumLength)
                throw new ArgumentException("请正确填写" + field.Label + "。");
            if (field.Kind == TaskFieldKind.Choice && !field.Choices.Any(c => c.Value == value)) throw new ArgumentException(field.Label + "无效。");
            if (field.Kind == TaskFieldKind.Number && (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ||
                n < field.Minimum || n > field.Maximum)) throw new ArgumentException(field.Label + "超出范围。");
        }
        var paths = config.CapabilityId == "program.start" ? [ExecutablePaths.ValidatePath(Value(config, "executable"))] : ExecutablePaths.Parse(Value(config, "executable"));
        if (config.CapabilityId == "program.start" && Number(config, "startupProtectionSeconds") % 1 != 0)
            throw new ArgumentException("启动保护时长必须为整数秒。");
        if (config.CapabilityId == "program.start" && Value(config, "workingDirectory") is { Length: > 0 } directory &&
            (!Path.IsPathFullyQualified(directory) || directory.StartsWith("\\\\", StringComparison.Ordinal)))
            throw new ArgumentException("工作目录必须是本机完整路径。");
        return paths;
    }
}
