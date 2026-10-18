using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class HardwareBrightnessCapabilities
{
    public const string Set = "display.hardware-brightness";
    public static TaskCapability Descriptor { get; } = new(Set, 1, TaskCapabilityKind.Action, "显示", "设置支持硬件亮度调节的屏幕",
        "对执行时主屏，或所有明确支持硬件亮度控制的屏幕设置一次亮度。",
        [new("target", "屏幕范围", TaskFieldKind.Choice, true, "执行时读取Windows逻辑显示布局，不绑定设备身份；主屏可能对应多块物理屏幕。", "primary", 7,
            [new("primary", "主屏"), new("all", "全部支持屏幕")]),
         new("percent", "亮度（%）", TaskFieldKind.Number, true, "0–100整数，映射到各屏幕报告的亮度范围；0可能难以看清。", "30", 3, [], Minimum: 0, Maximum: 100),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "由规则调度；暂停取消尚未发出的设置。", "0", 5, [], Minimum: 0, Maximum: 86400), .. BrightnessFadeSettings.Fields],
        "执行时查询硬件亮度支持与范围，每块支持屏幕直接或按渐变设置并读回；明确不支持的屏幕跳过。新亮度保留，条件结束、暂停或删除不恢复；关闭界面后后台继续执行。只调亮度，不改其他显示器设置，不持续控制、不重试。",
        "通常用于支持且启用DDC/CI的外接显示器，也可能包括其他支持此API的屏幕；不保证所有设备可用。坞站、虚拟显示、HDR或厂商软件可能限制支持或改变效果。查询/范围未知不写入，部分完成或读回不一致会说明。最多64个逻辑显示、每个8块物理屏幕、总计64块；整批默认最多10秒；启用渐变额外增加所选1–15秒，取消/超时可能已设置，原生阻塞期间保留句柄与单次准入直到返回，不回滚。");
    public static (string Target, int Percent, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set || invocation.SchemaVersion != 1 || invocation.Parameters is null || !BrightnessFadeSettings.ValidParameters(invocation.Parameters, "target", "percent", "delaySeconds") ||
            !invocation.Parameters.TryGetValue("target", out var target) || target is not ("primary" or "all") ||
            !invocation.Parameters.TryGetValue("percent", out var percent) || !int.TryParse(percent, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 0 or > 100 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择主屏或全部支持屏幕，并填写0–100整数亮度及0–86400整数延迟。");
        return (target, p, d);
    }
}
