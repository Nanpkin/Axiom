using Axiom.Domain;

namespace Axiom.Contracts;

public static class SessionLockStateCapabilities
{
    public const string State = "session.lock-state";
    public const string Resource = "current-session";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "会话", "当前Windows会话锁定状态",
        "当前活动交互会话已锁定或未锁定期间成立，允许用非条件组合。",
        [new("state", "会话状态", TaskFieldKind.Choice, true, "只读取当前用户会话，不切换或解锁会话。", "unlocked", 8,
            [new("locked", "已锁定"), new("unlocked", "未锁定")])],
        "共享会话通知后只读WTS连接/锁定元数据，无轮询。首次、重新启用、恢复及未知后的已知状态只建命令基线；已知成立可保持唤醒。",
        "非活动交互会话、读取或通知失败保持未知，未知的非仍是未知。仅表示Windows锁定状态，不证明人在场；不组合锁定或屏幕切换动作。", IsStateCondition: true);
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("state", out var state) || state is not ("locked" or "unlocked")) throw new ArgumentException("请选择已锁定或未锁定。");
        return state;
    }
}
