using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class HardwareBrightnessAdjustCapabilities
{
    public const string Adjust = "display.hardware-brightness-adjust";
    public static TaskCapability Descriptor { get; } = new(Adjust, 1, TaskCapabilityKind.Action, "显示", "提高或降低支持硬件亮度调节的屏幕亮度",
        "读取每块目标屏幕当前亮度，按指定整数百分点调整一次。",
        [HardwareBrightnessCapabilities.Descriptor.Fields[0], new("operation", "操作", TaskFieldKind.Choice, true, "相对每块屏幕自己的当前亮度调整。", "decrease", 8,
            [new("increase", "提高亮度"), new("decrease", "降低亮度")]),
         new("step", "调整幅度（百分点）", TaskFieldKind.Number, true, "1–100整数，例如40%提高5个百分点变为45%；到上下限即限幅。", "5", 3, [], Minimum: 1, Maximum: 100),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未执行的动作。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "每块目标先读取已知当前亮度，再限幅、一次写入和读回；未知不写。新值保留，条件结束或暂停不恢复。UI退出仍由后台执行，与原绝对亮度动作共享原生准入。",
        "沿用原亮度动作的支持性与范围限制，不绑定新设备或猜测读数。硬件范围可能有较粗粒度，调整量四舍五入到原生单位，粒度不足或已到端点可保持原值。部分失败会说明，不重试、回滚或持续压制；取消/超时可能已设置。");
    public static (string Target, string Operation, int Step, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Adjust || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 4 ||
            !invocation.Parameters.TryGetValue("target", out var target) || target is not ("primary" or "all") ||
            !invocation.Parameters.TryGetValue("operation", out var operation) || operation is not ("increase" or "decrease") ||
            !invocation.Parameters.TryGetValue("step", out var step) || !int.TryParse(step, NumberStyles.None, CultureInfo.InvariantCulture, out var s) || s is < 1 or > 100 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择屏幕范围与提高/降低亮度，并填写1–100整数幅度和0–86400整数延迟。");
        return (target, operation, s, d);
    }
}
