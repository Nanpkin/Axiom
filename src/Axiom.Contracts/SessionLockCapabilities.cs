using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class SessionLockCapabilities
{
    public const string Lock = "session.lock";
    public static TaskCapability Descriptor { get; } = new(Lock, 1, TaskCapabilityKind.Action, "会话", "锁定当前Windows会话",
        "向Windows请求锁定Axiom后台所在的当前用户会话一次。",
        [new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未发出的请求。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "只请求锁屏，不关闭程序、不注销或睡眠。Windows接受请求不代表已经完成锁定；之后需要正常登录解锁，Axiom不会自动解锁。关闭Axiom窗口后后台仍会按规则执行。",
        "请审阅触发条件和执行时段，避免使用电脑时意外锁屏。非活动交互会话、已锁定或状态无法确认时拒绝请求；系统或权限拒绝会说明。暂停、取消或超时不能撤销已经接受的锁定请求，不重试、不解锁，也不绕过登录。");
    public static int Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Lock || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("锁屏动作延迟须为0–86400的整数；不支持目标会话或自动解锁参数。");
        return d;
    }
}
