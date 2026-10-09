using Axiom.Domain;

namespace Axiom.Contracts;

public static class SessionPresenceCapabilities
{
    public const string State = "session.user-presence";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "会话", "Windows报告用户在场状态",
        "当前交互会话新报告在场、离开或不活跃后，执行一次动作。",
        [new("presence", "Windows报告状态", TaskFieldKind.Choice, true, "只按Windows原始定义，不是实际离座或自定空闲分钟。", "absent", 8,
            [new("present", "在场"), new("absent", "离开"), new("inactive", "不活跃")])],
        "只读会话系统通知，无轮询或输入监控。首次、重新启用、恢复及未知后的有效通知只建基线，不补动作；收到通知前保持未知。",
        "在场是系统报告输入活动，不活跃是系统活动超时；不能设定分钟或证明真人在座。只组合通知或默认播放设备音量/静音；不组合模拟输入、保持唤醒、屏幕、锁定、媒体控制或打开工具。音量/静音不自动恢复。");
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("presence", out var state) || !ValidResourceId(state))
            throw new ArgumentException("请选择Windows报告在场、离开或不活跃。");
        return state;
    }
    public static bool ValidResourceId(string resource) => resource is "present" or "absent" or "inactive";
}
