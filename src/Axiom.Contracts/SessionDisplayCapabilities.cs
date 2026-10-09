using Axiom.Domain;

namespace Axiom.Contracts;

public static class SessionDisplayCapabilities
{
    public const string State = "screen.state";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "显示", "当前会话屏幕电源状态",
        "Windows报告当前交互会话的屏幕亮、暗或关后，执行一次动作。",
        [new("state", "屏幕状态", TaskFieldKind.Choice, true, "整体会话报告，不选择具体显示器。", "off", 3,
            [new("on", "亮着"), new("dim", "已变暗"), new("off", "已关闭")])],
        "只读系统通知，无轮询。首次、重新启用、睡眠恢复或未知后的首个有效通知只建基线，不补发动作；等待通知时保持未知。",
        "不证明锁屏、黑画面、屏保、用户离开或每块物理屏状态；远程会话不代表客户端物理屏幕。变暗通知可能不出现，现代待机可能暂停动作。不组合保持唤醒或显示设置动作。");
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("state", out var state) || !ValidResourceId(state))
            throw new ArgumentException("请选择亮着、已变暗或已关闭。");
        return state;
    }
    public static bool ValidResourceId(string resource) => resource is "on" or "dim" or "off";
}
