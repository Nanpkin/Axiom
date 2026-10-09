using Axiom.Domain;

namespace Axiom.Contracts;

public static class ForegroundScreenCoverCapabilities
{
    public const string Covers = "window.foreground-covers-screen";
    public const string Resource = "current";
    public static TaskCapability Descriptor { get; } = new(Covers, 1, TaskCapabilityKind.Detection, "窗口", "当前前台窗口覆盖整块屏幕",
        "合格前台应用的DWM可见边框覆盖所在显示器整块区域期间成立，无需选择程序。", [],
        "按系统前台、窗口及显示变化通知只读当前窗口，无轮询。首次、前台选择变化或未知恢复只建命令基线；已知成立可保持唤醒，离开、未知或暂停后释放。",
        "这是物理像素几何，不证明独占全屏、游戏模式或视频播放。最大化只覆盖工作区时不成立；自动隐藏任务栏使工作区等于整屏时可能成立。隐藏、最小化、cloaked、桌面、任务栏、Axiom自身及无合格应用为不成立；读取或通知失败保持未知，不调整窗口。");
    public static void Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Covers || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 0)
            throw new ArgumentException("当前前台窗口覆盖屏幕条件无需程序、显示器或阈值参数。");
    }
}
