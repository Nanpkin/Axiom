using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record WindowOnceSpec(int StartMinute, int EndMinute, int Days, int RetryMinutes = 5, int MaxRetries = 3)
{
    public int Duration => EndMinute > StartMinute ? EndMinute - StartMinute : 1440 - StartMinute + EndMinute;
    public string Resource => FormattableString.Invariant($"{StartMinute / 60:00}:{StartMinute % 60:00}-{EndMinute / 60:00}:{EndMinute % 60:00}/{Days:X2}/{RetryMinutes}/{MaxRetries}");
    public void Validate()
    {
        if (StartMinute is < 0 or > 1439 || EndMinute is < 0 or > 1439 || StartMinute == EndMinute || Days is < 1 or > 127 || RetryMinutes is < 1 or > 1440 || MaxRetries is < 0 or > 100)
            throw new ArgumentException("请选择不同的有效起止时间、至少一天，以及有效重试间隔和次数。");
    }
    public DateOnly? WindowDate(DateTimeOffset at, TimeZoneInfo zone)
    {
        Validate();
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        var minute = local.TimeOfDay.TotalMinutes;
        var date = DateOnly.FromDateTime(local);
        if (StartMinute < EndMinute)
        {
            if (minute < StartMinute || minute >= EndMinute) return null;
        }
        else if (minute < EndMinute) date = date.AddDays(-1);
        else if (minute < StartMinute) return null;
        return (Days & (1 << (int)date.DayOfWeek)) != 0 ? date : null;
    }
    // An uncompleted current window is immediately eligible, including a late boot.
    public DateTimeOffset Next(DateTimeOffset after, TimeZoneInfo zone, DateOnly? lastOffered = null)
    {
        if (WindowDate(after, zone) is { } current && (lastOffered is null || current > lastOffered.Value)) return after;
        foreach (var at in Boundaries(after, zone).Order())
            if (at > after && WindowDate(at, zone) is { } date && (lastOffered is null || date > lastOffered.Value)) return at;
        throw new InvalidOperationException("当前时区内没有下一次有效时段，请检查系统日期和时区。");
    }
    public DateTimeOffset NextBoundary(DateTimeOffset after, TimeZoneInfo zone)
    {
        Validate();
        foreach (var at in Boundaries(after, zone).Order())
            if (at > after && WindowDate(at.AddTicks(-1), zone) != WindowDate(at, zone)) return at;
        throw new InvalidOperationException("当前时区内没有下一次有效时段边界，请检查系统日期和时区。");
    }
    private IEnumerable<DateTimeOffset> Boundaries(DateTimeOffset after, TimeZoneInfo zone)
    {
        Validate();
        var first = TimeZoneInfo.ConvertTime(after, zone).Date.AddDays(-1);
        for (var day = 0; day < 17; day++)
        {
            var date = first.AddDays(day);
            if ((Days & (1 << (int)date.DayOfWeek)) == 0) continue;
            var start = DateTime.SpecifyKind(date.AddMinutes(StartMinute), DateTimeKind.Unspecified);
            foreach (var at in Resolve(start)) yield return at;
            foreach (var at in Resolve(start.AddMinutes(Duration))) yield return at;
        }
        IEnumerable<DateTimeOffset> Resolve(DateTime local)
        {
            // A missing boundary takes effect at the first existing local minute after the gap.
            for (var minute = 0; zone.IsInvalidTime(local); minute++)
            {
                if (minute == 1440) throw new InvalidOperationException("当前时区的时间跳变超出支持范围。");
                local = local.AddMinutes(1);
            }
            if (!zone.IsAmbiguousTime(local))
            {
                yield return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
                yield break;
            }
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            foreach (var offset in offsets) yield return new DateTimeOffset(local, offset).ToUniversalTime();
            // A repeated hour can leave/re-enter a partial-hour window at the rollback itself.
            var beginning = local;
            for (var minute = 0; zone.IsAmbiguousTime(beginning.AddMinutes(-1)); minute++)
            {
                if (minute == 1440) throw new InvalidOperationException("当前时区的重复时段超出支持范围。");
                beginning = beginning.AddMinutes(-1);
            }
            yield return new DateTimeOffset(beginning, offsets.Min()).ToUniversalTime();
        }
    }
}

public static class WindowOnceCapabilities
{
    public const string Due = "time.window-once";
    private static readonly string[] DayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    public static TaskCapability Descriptor { get; } = new(Due, 1, TaskCapabilityKind.Detection, "时间", "在选定时段内执行一次",
        "时段内执行，晚开机也能执行；本时段成功后不重复。",
        [new("startHour", "开始小时", TaskFieldKind.Number, true, "24小时制，包含开始时刻。", "6", 2, [], Minimum: 0, Maximum: 23),
         new("startMinute", "开始分钟", TaskFieldKind.Number, true, "0–59的整数。", "0", 2, [], Minimum: 0, Maximum: 59),
         new("endHour", "结束小时（不包含）", TaskFieldKind.Number, true, "早于开始表示跨午夜，起止不能相同。", "18", 2, [], Minimum: 0, Maximum: 23),
         new("endMinute", "结束分钟", TaskFieldKind.Number, true, "超过结束时刻不补执行。", "0", 2, [], Minimum: 0, Maximum: 59),
         .. Enumerable.Range(0, 7).Select(i => (i + 1) % 7).Select(d => new TaskField("day" + d, DayNames[d], TaskFieldKind.Boolean, true,
             "按时段起始日。", "true", 5, [], Group: "重复日期")),
         new("retryMinutes", "失败后重试间隔（分钟）", TaskFieldKind.Number, true,
             "1–1440整数分钟；只在时段内重试明确失败。", "5", 4, [], Minimum: 1, Maximum: 1440, Group: "失败重试"),
         new("maxRetries", "最多重试次数", TaskFieldKind.Number, true,
             "首次之外的次数；0关闭，最多100。", "3", 3, [], Minimum: 0, Maximum: 100, Group: "失败重试")],
        "例如每天6–18点，8点开机也会执行；成功后保存本规则的时段记录，重启后台、电脑或重新启用不会重复。按Windows本地时区，不唤醒电脑，UI退出后继续。",
        "结束不补发、不撤回动作。失败/拒绝按配置重试整动作，可能重做部分已完成步骤；未知、失联或异常退出，本时段不重发。跨午夜按开始日期计次，无节假日判断。暂停/修改保留成功记录。");
    public static WindowOnceSpec Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Due || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 13)
            throw new ArgumentException("时段执行配置无效。");
        int Number(string field, int maximum)
        {
            if (!invocation.Parameters.TryGetValue(field, out var value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0 || number > maximum)
                throw new ArgumentException("时段起止和重试配置必须为范围内的整数。");
            return number;
        }
        var days = 0;
        for (var day = 0; day < 7; day++)
        {
            if (!invocation.Parameters.TryGetValue("day" + day, out var value) || value is not ("true" or "false")) throw new ArgumentException("星期选择无效。");
            if (value == "true") days |= 1 << day;
        }
        var spec = new WindowOnceSpec(Number("startHour", 23) * 60 + Number("startMinute", 59), Number("endHour", 23) * 60 + Number("endMinute", 59), days,
            Number("retryMinutes", 1440), Number("maxRetries", 100));
        spec.Validate(); return spec;
    }
    public static string Resource(string taskId, WindowOnceSpec spec)
    {
        if (!Guid.TryParseExact(taskId, "D", out _)) throw new ArgumentException("时段执行任务标识无效。");
        spec.Validate(); return taskId + "@" + spec.Resource;
    }
    public static (string TaskId, WindowOnceSpec Spec) ParseResource(string resource)
    {
        if (resource.Length is < 55 or > 60 || resource[36] != '@' || !Guid.TryParseExact(resource[..36], "D", out _)) throw new ArgumentException("时段执行资源无效。");
        var parts = resource[37..].Split('/');
        if (parts.Length != 4 || parts[0].Length != 11 || parts[1].Length != 2 ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var retry) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var maximum)) throw new ArgumentException("时段执行资源无效。");
        var value = parts[0] + "/" + parts[1];
        if (value[2] != ':' || value[5] != '-' || value[8] != ':' || value[11] != '/' ||
            !int.TryParse(value.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var sh) || sh is < 0 or > 23 ||
            !int.TryParse(value.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var sm) || sm is < 0 or > 59 ||
            !int.TryParse(value.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var eh) || eh is < 0 or > 23 ||
            !int.TryParse(value.AsSpan(9, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var em) || em is < 0 or > 59 ||
            !int.TryParse(value.AsSpan(12, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var days)) throw new ArgumentException("时段执行资源无效。");
        var spec = new WindowOnceSpec(sh * 60 + sm, eh * 60 + em, days, retry, maximum); spec.Validate();
        if (resource[37..] != spec.Resource) throw new ArgumentException("时段执行资源格式无效。");
        return (resource[..36], spec);
    }
    public static string Label(string resource)
    {
        var spec = ParseResource(resource).Spec;
        var days = spec.Days == 127 ? "每天" : spec.Days == 62 ? "周一至周五" : string.Join("、", Enumerable.Range(0, 7).Where(d => (spec.Days & (1 << d)) != 0).Select(d => DayNames[d]));
        return $"{days} {spec.StartMinute / 60:00}:{spec.StartMinute % 60:00}–{(spec.EndMinute < spec.StartMinute ? "次日" : "")}{spec.EndMinute / 60:00}:{spec.EndMinute % 60:00}，成功后本时段不重复";
    }
}
