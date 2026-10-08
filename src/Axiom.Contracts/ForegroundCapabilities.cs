using Axiom.Domain;

namespace Axiom.Contracts;

public static class ForegroundCapabilities
{
    public const string Active = "process.foreground";
    public static TaskCapability Descriptor { get; } = new(Active, 1, TaskCapabilityKind.Detection, "程序", "程序成为前台",
        "切换到选定程序的窗口时条件成立，不读取窗口标题或内容。",
        [new("processNames", "检测的程序", TaskFieldKind.ProcessSelection, true, "多选程序名；当前用户会话中的任意一个成为前台即满足。", null, 32768, [])],
        "按需共享 Windows 前台通知，无轮询；启动/重连时已有前台状态不补执行，在所选程序间切换保持成立，不重复触发；离开目标或无法核验时取消等待。",
        "按程序名匹配，不等同于用户正在操作。为避免输入错发，此检测不提供发送快捷键动作。");
    public static ProviderSample[] Samples(ProviderResource[] resources, string? processName) => [.. resources.Select(r => new ProviderSample(r,
        processName is null ? StateQuality.Unknown : StateQuality.Known, "axiom.bool", 1,
        Boolean: processName is null ? null : string.Equals(processName, r.ResourceId, StringComparison.OrdinalIgnoreCase),
        Reason: processName is null ? "无法核验前台应用，可能处于安全桌面或权限不足；不会当作已离开目标。" : null))
        // Preserve Any(A, B) while moving directly from A to B; never synthesize a false edge between selected applications.
        .OrderByDescending(s => s.Boolean == true)];
}

