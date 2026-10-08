using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record WindowedIntervalSpec(int Minutes, int StartMinute, int EndMinute, int Days)
{
    public int Duration => EndMinute > StartMinute ? EndMinute - StartMinute : 1440 - StartMinute + EndMinute;
    public string Resource => FormattableString.Invariant($"{Minutes}@{StartMinute / 60:00}:{StartMinute % 60:00}-{EndMinute / 60:00}:{EndMinute % 60:00}/{Days:X2}");
    public void Validate()
    {
        if (Minutes is < 1 or > 1440 || StartMinute is < 0 or > 1439 || EndMinute is < 0 or > 1439 || StartMinute == EndMinute ||
            Days is < 1 or > 127 || Minutes >= Duration) throw new ArgumentException("请选择不同的起止时刻、至少一天，以及短于时段的整数间隔。");
    }
    public bool Contains(DateTimeOffset at, TimeZoneInfo zone)
    {
        Validate(); var local = TimeZoneInfo.ConvertTime(at, zone).DateTime; var minute = local.TimeOfDay.TotalMinutes;
        var date = local.Date;
        if (StartMinute < EndMinute)
        { if (minute < StartMinute || minute >= EndMinute) return false; }
        else
        {
            if (minute >= StartMinute) { }
            else if (minute < EndMinute) date = date.AddDays(-1);
            else return false;
        }
        return (Days & (1 << (int)date.DayOfWeek)) != 0;
    }
    public DateTimeOffset Next(DateTimeOffset after, TimeZoneInfo zone, DateTime? lastFiredLocal = null)
    {
        Validate();
        var cursor = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        if (lastFiredLocal is { } fired && fired > cursor) cursor = fired;
        // Include the previous start day for overnight windows. Weekday selection belongs to that start day.
        var firstDate = cursor.Date.AddDays(-1);
        for (var day = 0; day < 16; day++)
        {
            var date = firstDate.AddDays(day);
            if ((Days & (1 << (int)date.DayOfWeek)) == 0) continue;
            var start = DateTime.SpecifyKind(date.AddMinutes(StartMinute), DateTimeKind.Unspecified); var end = start.AddMinutes(Duration);
            var index = Math.Max(1, (int)Math.Floor((cursor - start).TotalMinutes / Minutes) + 1);
            for (var local = start.AddMinutes((long)index * Minutes); local < end; local = local.AddMinutes(Minutes))
            {
                if (zone.IsInvalidTime(local)) continue;
                var utc = zone.IsAmbiguousTime(local)
                    ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
                    : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
                if (utc > after) return utc;
            }
        }
        throw new InvalidOperationException("当前时区内没有下一次有效时段提醒，请检查系统日期和时区。");
    }
}
public static class WindowedIntervalCapabilities
{
    public const string Due = "time.windowed-interval";
    private static readonly string[] DayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    public static TaskCapability Descriptor { get; } = new(Due, 1, TaskCapabilityKind.Detection, "时间", "在选定时段内定期执行",
        "按本地时段起点加间隔产生未来钟点，适合工作时间内的定期提醒。",
        [new("minutes", "间隔（分钟）", TaskFieldKind.Number, true, "整数，必须短于时段长度；第一次在起点之后一个间隔。", "30", 4, [], Minimum: 1, Maximum: 1440),
         new("startHour", "开始小时", TaskFieldKind.Number, true, "24小时制。", "9", 2, [], Minimum: 0, Maximum: 23),
         new("startMinute", "开始分钟", TaskFieldKind.Number, true, "0–59的整数。", "0", 2, [], Minimum: 0, Maximum: 59),
         new("endHour", "结束小时（不包含）", TaskFieldKind.Number, true, "结束早于开始表示跨午夜，不支持起止相同。", "18", 2, [], Minimum: 0, Maximum: 23),
         new("endMinute", "结束分钟", TaskFieldKind.Number, true, "结束时刻不触发，延迟动作可能在时段外执行。", "0", 2, [], Minimum: 0, Maximum: 59),
         .. Enumerable.Range(0, 7).Select(i => (i + 1) % 7).Select(d => new TaskField("day" + d, DayNames[d], TaskFieldKind.Boolean, true,
             "选择时段开始所在的星期；跨午夜的后半段属于前一天。", d is >= 1 and <= 5 ? "true" : "false", 5, [], Group: "重复日期"))],
        "例如9–18点每30分钟，机会为9:30至17:30；10:10启用则等10:30。只等待未来钟点，时钟/时区/恢复变化后重算，不补发错过机会；最多允许一分钟调度延迟且仍须在时段内。UI退出规则继续。",
        "仅限制此定时检测的触发机会，不限制其他规则或撤回已开始的动作；动作延迟可能跨过结束时刻。无节假日判断、不唤醒电脑。夏令时缺失钟点跳过、重复钟点只取第一次；同一订阅已发出的本地钟点不重发，重启不保存此历史。");
    public static WindowedIntervalSpec Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Due || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 12) throw new ArgumentException("时段定时配置无效。");
        int Number(string field, int max)
        {
            if (!invocation.Parameters.TryGetValue(field, out var value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0 || n > max)
                throw new ArgumentException("时段及间隔必须为有效整数。");
            return n;
        }
        var days = 0;
        for (var day = 0; day < 7; day++)
        {
            if (!invocation.Parameters.TryGetValue("day" + day, out var enabled) || enabled is not ("true" or "false")) throw new ArgumentException("星期选择无效。");
            if (enabled == "true") days |= 1 << day;
        }
        var spec = new WindowedIntervalSpec(Number("minutes", 1440), Number("startHour", 23) * 60 + Number("startMinute", 59), Number("endHour", 23) * 60 + Number("endMinute", 59), days);
        spec.Validate(); return spec;
    }
    public static WindowedIntervalSpec ParseResource(string resource)
    {
        if (resource.Length > 24) throw new ArgumentException("时段资源无效。");
        var parts = resource.Split(['@', ':', '-', '/']);
        if (parts.Length != 6 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var interval) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sh) || sh is < 0 or > 23 ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sm) || sm is < 0 or > 59 ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var eh) || eh is < 0 or > 23 ||
            !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var em) || em is < 0 or > 59 ||
            !int.TryParse(parts[5], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var days)) throw new ArgumentException("时段资源无效。");
        var spec = new WindowedIntervalSpec(interval, sh * 60 + sm, eh * 60 + em, days); spec.Validate();
        if (spec.Resource != resource) throw new ArgumentException("时段资源格式无效。"); return spec;
    }
    public static string Label(string resource)
    {
        var spec = ParseResource(resource);
        var days = spec.Days == 62 ? "周一至周五" : spec.Days == 127 ? "每天" : string.Join("、", Enumerable.Range(0, 7).Where(d => (spec.Days & (1 << d)) != 0).Select(d => DayNames[d]));
        return $"{days} {spec.StartMinute / 60:00}:{spec.StartMinute % 60:00}–{(spec.EndMinute < spec.StartMinute ? "次日" : "")}{spec.EndMinute / 60:00}:{spec.EndMinute % 60:00}，每{spec.Minutes}分钟";
    }
}
