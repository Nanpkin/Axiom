using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class DisplayOffCapabilities
{
    public const string Request = "display.off-request";
    public static TaskCapability Descriptor { get; } = new(Request, 1, TaskCapabilityKind.Action, "显示", "请求关闭当前会话屏幕",
        "向Windows发送一次屏幕关闭请求，电脑与后台工作继续运行。",
        [new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未发出的请求。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "适用于快捷键或锁屏事件；活动会话已锁定也可请求。不锁屏、不睡眠、不修改电源方案。关闭Axiom窗口后后台仍执行。",
        "Windows接受请求不代表物理屏幕已关闭；输入可能立即唤醒屏幕，外接屏幕驱动和保持屏幕唤醒请求可能影响效果。不持续压制唤醒、不自动重试。暂停、取消或超时无法撤销已经发出的请求，也不会自动打开屏幕；此动作不保证电脑持续保持唤醒。");
    public static int Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Request || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("关闭屏幕动作延迟须为0–86400的整数；不支持目标会话、持续关闭或恢复参数。");
        return d;
    }
}
