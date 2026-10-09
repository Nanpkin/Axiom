using Axiom.Domain;

namespace Axiom.Contracts;

public static class ForegroundWindowLayoutCapabilities
{
    public const string Layout = "window.foreground-layout";
    public static TaskCapability Descriptor { get; } = new(Layout, 1, TaskCapabilityKind.Action, "窗口", "整理当前前台窗口",
        "快捷键执行开始时捕获一个前台窗口，摆到它当前显示器的工作区，不需要选择程序。",
        [new("layout", "窗口布局", TaskFieldKind.Choice, true, "只整理捕获的窗口；最大化或最小化先还原。", "left", 16,
            [new("left", "左半屏"), new("right", "右半屏"), new("top-left", "左上四分之一"), new("top-right", "右上四分之一"),
             new("bottom-left", "左下四分之一"), new("bottom-right", "右下四分之一"), new("center", "中央（80%）")])],
        "仅支持快捷键。核验当前用户、会话和窗口所属实例；每次写入前确认仍是同一个前台窗口，切走或身份未知后不再写。一次还原、布局与读回，不重试、不轮询。",
        "只处理当前可见、无拥有者的顶层应用窗口，不处理Axiom自身、桌面、任务栏或确认对话框。程序限制大小、位置或操作中切走可能使结果未确认；取消、超时或部分失败可能已还原或移动。暂停/结束不恢复旧布局，之后手动调整不受干预。");
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Layout || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("layout", out var layout) || layout is not ("left" or "right" or "top-left" or "top-right" or "bottom-left" or "bottom-right" or "center"))
            throw new ArgumentException("请选择支持的前台窗口布局；不接受程序路径、窗口标识或其他参数。");
        return layout;
    }
}
