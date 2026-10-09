using Axiom.Domain;

namespace Axiom.Contracts;

public static class ForegroundWindowMonitorCapabilities
{
    public const string Move = "window.foreground-monitor";
    public static TaskCapability Descriptor { get; } = new(Move, 1, TaskCapabilityKind.Action, "窗口", "把当前前台窗口移到显示器",
        "快捷键执行开始捕获一个前台窗口，移到目标显示器工作区中央80%，无需选择程序。",
        [new("monitor", "目标显示器", TaskFieldKind.Choice, true, "按执行时桌面横、纵坐标排序循环；上一屏/下一屏只有一屏时不做任何调整。", "next", 16,
            [new("primary", "主显示器"), new("previous", "上一个显示器"), new("next", "下一个显示器")])],
        "只支持快捷键，核验同用户、会话和捕获窗口身份，每次写前仍须前台且显示器布局未变。最大化/最小化先还原，再移动并读回；选主屏时即使已在主屏也会居中80%。",
        "不操作Axiom自身、桌面、任务栏或有拥有者对话框。身份未知、切走、显示器失联或拓扑变化后不再写，也不寻找新的前台或同应用其他窗口。3秒事件确认，无轮询、重试或旧布局恢复；取消、超时或部分失败可能已还原/移动。程序限制大小位置或混合DPI可能影响结果。");
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Move || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("monitor", out var monitor) || monitor is not ("primary" or "previous" or "next"))
            throw new ArgumentException("请选择主屏、上一屏或下一屏；不接受程序路径、窗口标识或其他参数。");
        return monitor;
    }
}
