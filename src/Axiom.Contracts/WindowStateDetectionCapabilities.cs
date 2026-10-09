using Axiom.Domain;

namespace Axiom.Contracts;

public static class WindowStateDetectionCapabilities
{
    public const string StateIs = "window.state-is";
    public static TaskCapability Descriptor { get; } = new(StateIs, 1, TaskCapabilityKind.Detection,
        "窗口", "程序窗口状态", "选定程序的任意一个可见主窗口处于最小化、最大化或普通状态时条件成立。",
        [new("executable", "检测的程序", TaskFieldKind.ExecutableSelection, true,
            "多选1–32个完整exe路径，精确匹配当前用户会话；不读取窗口标题。", null, ExecutablePaths.MaximumTextLength, []),
         new("state", "窗口状态", TaskFieldKind.Choice, true,
            "普通指既未最小化也未最大化；隐藏或没有窗口不算普通。", "maximized", 16,
            [new("minimized", "最小化"), new("maximized", "最大化"), new("normal", "普通状态")])],
        "按需共享窗口通知；任意匹配窗口满足即成立。首次连接或未知后恢复只建立一次性动作基线；持续动作可使用已知初始状态。暂停、删除或断开释放订阅，关闭Axiom窗口仍检测。",
        "只认可见顶层且没有拥有者的主窗口，最小化仍算可见。最大化不等于全屏演示；不读取标题或正文。按通知读当前状态，短暂往返变化可能合并。隐藏、销毁与最小化分别处理。身份无法核验保持相关状态未知，不证明用户手动调整；不与整理窗口状态或移屏动作组合，以避免自身反复触发。");

    public static (string[] Paths, string State) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != StateIs || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("executable", out var paths) ||
            !invocation.Parameters.TryGetValue("state", out var state) || state is not ("minimized" or "maximized" or "normal"))
            throw new ArgumentException("窗口状态检测参数无效。");
        return (ExecutablePaths.Parse(paths), state);
    }

    public static string Resource(string path, string state) => state + "|" + ExecutablePaths.ValidatePath(path).ToLowerInvariant();

    public static (string Path, string State) ParseResource(string resource)
    {
        var separator = resource.IndexOf('|');
        if (separator < 0 || resource[..separator] is not ("minimized" or "maximized" or "normal"))
            throw new ArgumentException("窗口状态检测资源无效。");
        return (ExecutablePaths.ValidatePath(resource[(separator + 1)..]), resource[..separator]);
    }
}
