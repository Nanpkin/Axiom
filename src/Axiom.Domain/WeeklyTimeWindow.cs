namespace Axiom.Domain;

// Local wall-clock admission window. Overnight intervals belong to their starting weekday.
// No timer or catch-up: callers check on a fresh edge and immediately before dispatch.
public sealed record WeeklyTimeWindow(int StartMinute, int EndMinute, int Days)
{
    public void Validate()
    {
        if (StartMinute is < 0 or > 1439 || EndMinute is < 0 or > 1439 || StartMinute == EndMinute || Days is < 1 or > 127)
            throw new ArgumentException("请选择不同的起止时刻和至少一个星期日期。");
    }

    public bool Contains(DateTimeOffset at, TimeZoneInfo zone)
    {
        Validate();
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var minute = local.TimeOfDay.TotalMinutes;
        var day = (int)local.DayOfWeek;
        if (StartMinute < EndMinute)
        {
            if (minute < StartMinute || minute >= EndMinute) return false;
        }
        else if (minute < EndMinute) day = (day + 6) % 7;
        else if (minute < StartMinute) return false;
        return (Days & (1 << day)) != 0;
    }
}
