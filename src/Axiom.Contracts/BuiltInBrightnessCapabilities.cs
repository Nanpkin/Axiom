using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class BuiltInBrightnessCapabilities
{
    public const string Set = "display.brightness";
    public static TaskCapability Descriptor { get; } = new(Set, 1, TaskCapabilityKind.Action, "显示", "设置支持的内置屏幕亮度",
        "对执行时可确认支持Windows WMI亮度接口的所有活动内置面板设置一次亮度。",
        [new("percent", "亮度（%）", TaskFieldKind.Number, true, "0–100整数；0可能使屏幕难以看清，请谨慎选择。只修改支持的内置面板，不选择外接屏幕。", "40", 3, [], Minimum: 0, Maximum: 100),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "由规则调度；暂停取消尚未发出的设置。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "执行时核对内置连接、活动状态和可写WMI方法；每块匹配面板设置一次并读回。新亮度会保留，之后可手动修改；条件结束、暂停或删除不恢复，不持续控制、不自动重试。关闭Axiom窗口后后台仍会执行。",
        "仅覆盖支持WmiMonitorBrightness接口的内置面板；外接DDC/CI显示器不在范围。无支持接口、身份不明或权限不足会失败；多面板部分成功会说明，不能回滚已改变的面板。驱动可能取近似值，读回不一致会报告失败。自适应亮度、HDR或厂商软件可能随后改变效果。取消或超时可能已设置，结果未确认时不重试或恢复。");
    public static (int Percent, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("percent", out var percent) || !int.TryParse(percent, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 0 or > 100 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请填写0–100的整数亮度和0–86400的整数延迟；此动作不支持外屏或指定设备标识。");
        return (p, d);
    }
}
