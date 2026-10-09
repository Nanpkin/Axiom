using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class IntervalCapabilities
{
    public const string Elapsed = "interval.elapsed";
    public static TaskCapability Descriptor { get; } = new(Elapsed, 1, TaskCapabilityKind.Detection, "时间", "每隔一段时间",
        "检测就绪后，每隔指定分钟产生一次事件。",
        [new("minutes", "间隔（分钟）", TaskFieldKind.Number, true, "1–1440 分钟；从检测就绪开始计算，不是每天某个钟点。", "30", 4, [], Minimum: 1, Maximum: 1440)],
        "只在有启用规则时计时。暂停、重启或检测重新连接后重新计时；修改规则可能重连检测。UI退出仍由后台计时。挂起后至多处理一个已到期事件，不连续补发遗漏次数。",
        "这是重复的一次动作，不保持或恢复系统设置。动作仍忙时不会积压补执行；请谨慎选择反复打开窗口、关闭程序等动作，第一次也须等待完整间隔。不保证睡眠中执行，也不会唤醒电脑。");
    public static int Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Elapsed || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("minutes", out var text)) throw new ArgumentException("计时配置无效。");
        return Parse(text);
    }
    public static int Parse(string text)
    {
        if (text.Length is < 1 or > 4 || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes is < 1 or > 1440)
            throw new ArgumentException("计时间隔须为1–1440的整数分钟。");
        return minutes;
    }
    public static string Resource(int minutes) => minutes.ToString(CultureInfo.InvariantCulture);
}
