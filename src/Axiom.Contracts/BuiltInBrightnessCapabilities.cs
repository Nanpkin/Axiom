using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class BuiltInBrightnessCapabilities
{
    public const string Set = "display.brightness";
    public static TaskCapability Descriptor { get; } = new(Set, 1, TaskCapabilityKind.Action, "显示", "设置屏幕亮度（自动选择）",
        "优先设置所有活动内置面板；明确没有内置亮度接口时，自动使用主屏硬件亮度。",
        [new("percent", "亮度（%）", TaskFieldKind.Number, true, "0–100整数；0可能使屏幕难以看清。自动选择支持的内屏，明确没有内屏接口时选择主屏；指定全部硬件屏请使用硬件亮度动作。", "40", 3, [], Minimum: 0, Maximum: 100),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "由规则调度；暂停取消尚未发出的设置。", "0", 5, [], Minimum: 0, Maximum: 86400), .. BrightnessFadeSettings.Fields],
        "执行时优先核对所有活动内置面板及WMI可写接口；明确不存在内置亮度接口或只有外接目标时，使用主屏硬件亮度。每块目标直接或按渐变设置并读回。新亮度保留，条件结束、暂停或删除不恢复，不持续控制、不重试未确认写入。关闭Axiom窗口后后台仍会执行。",
        "内屏查询、身份或权限未知时不切换到外屏；已找到内屏但写入或读回失败时也不切换。主屏硬件路径需要DDC/CI等硬件支持且已知有效范围；不自动选择全部硬件屏。部分完成会说明，不能回滚已改变的目标。驱动、自适应亮度、HDR或厂商软件可能影响效果；取消或超时可能已设置，未确认不重试或恢复。");
    public static (int Percent, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set || invocation.SchemaVersion != 1 || invocation.Parameters is null || !BrightnessFadeSettings.ValidParameters(invocation.Parameters, "percent", "delaySeconds") ||
            !invocation.Parameters.TryGetValue("percent", out var percent) || !int.TryParse(percent, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 0 or > 100 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请填写0–100的整数亮度和0–86400的整数延迟；屏幕自动选择，不接受指定设备标识或额外范围参数。");
        return (p, d);
    }
}
