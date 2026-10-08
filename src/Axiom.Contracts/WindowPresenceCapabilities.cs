using Axiom.Domain;

namespace Axiom.Contracts;

public static class WindowPresenceCapabilities
{
    public const string Visible = "window.visible";
    public static TaskCapability Descriptor { get; } = new(Visible, 1, TaskCapabilityKind.Detection, "窗口", "程序窗口出现",
        "选定程序拥有可见主窗口时条件成立，不读取标题。",
        [new("processNames", "检测的程序", TaskFieldKind.ProcessSelection, true, "多选程序名，匹配当前用户会话的任意一个可见主窗口；最小化仍算可见。", null, 32768, [])],
        "共享窗口通知；首次读取已有窗口不补执行。目标程序仍有其他可见主窗口时不重复触发，全部隐藏或销毁后才结束条件。",
        "窗口消失不表示程序退出，也不证明用户手动关闭。拥有者弹窗不算主窗口；不能据此判断用户是否正在操作。");
}

