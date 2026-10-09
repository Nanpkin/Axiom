using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record OnceTimeSpec(DateTime Local)
{
    public string Resource => Local.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
    public DateTimeOffset? At(TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(Local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) return null;
        return zone.IsAmbiguousTime(local)
            ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}
public static class OnceTimeCapabilities
{
    public const string Due = "time.once";
    public static TaskCapability Descriptor { get; } = new(Due, 1, TaskCapabilityKind.Detection, "时间", "在指定日期执行一次",
        "按Windows当前本地时区，填写一个具体日期和时刻。",
        [new("year", "年份", TaskFieldKind.Number, true, "四位年份。", null, 4, [], Minimum: 2000, Maximum: 9998),
         new("month", "月份", TaskFieldKind.Number, true, "1–12的整数。", null, 2, [], Minimum: 1, Maximum: 12),
         new("day", "日期", TaskFieldKind.Number, true, "所选月份中的日期。", null, 2, [], Minimum: 1, Maximum: 31),
         new("hour", "小时（24小时制）", TaskFieldKind.Number, true, "0–23的整数。", "9", 2, [], Minimum: 0, Maximum: 23),
         new("minute", "分钟", TaskFieldKind.Number, true, "0–59的整数。", "0", 2, [], Minimum: 0, Maximum: 59)],
        "只等未来时刻；UI退出仍由后台执行。时钟、时区或恢复后重算，已过期不补发。允许一分钟调度延迟，更晚跳过；夏令时不存在的时刻跳过，重复时刻只取第一次。",
        "不唤醒电脑；到期只交给规则判断一次，不保证动作成功或忙时补执行。规则保留供查看/暂停/删除。重启不持久化到期状态，日期或时区回拨后重新订阅可能再次到期。");
    public static OnceTimeSpec Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Due || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 5)
            throw new ArgumentException("单次定时参数无效。");
        int Read(string name, int min, int max)
        {
            if (!invocation.Parameters.TryGetValue(name, out var value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
                throw new ArgumentException("请填写有效的整数日期和时间。");
            return number;
        }
        var year = Read("year", 2000, 9998); var month = Read("month", 1, 12); var day = Read("day", 1, DateTime.DaysInMonth(year, month));
        return new(new(year, month, day, Read("hour", 0, 23), Read("minute", 0, 59), 0, DateTimeKind.Unspecified));
    }
    public static OnceTimeSpec ParseResource(string value)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) || local.Year is < 2000 or > 9998)
            throw new ArgumentException("单次定时资源无效。");
        return new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified));
    }
    public static string Label(string resource) => ParseResource(resource).Local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "（一次）";
}
