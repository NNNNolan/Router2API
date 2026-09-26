namespace Router.Host.Services;

/// <summary>宿主使用的五/六字段中国标准时间 Cron 解析器。</summary>
internal static class CronSchedule
{
    private static readonly TimeZoneInfo ChinaTimeZone = ResolveChinaTimeZone();

    public static DateTimeOffset GetNext(string expression, DateTimeOffset now)
    {
        if (string.Equals(expression.Trim(), "@hourly", StringComparison.OrdinalIgnoreCase))
            expression = "0 0 * * * *";

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length == 5) fields = ["0", .. fields];
        if (fields.Length != 6) throw new FormatException("cron 必须包含五个或六个字段");

        var seconds = ParseNumber(fields[0], "seconds", 0, 59);
        var localNow = TimeZoneInfo.ConvertTime(now, ChinaTimeZone).DateTime;
        var start = new DateTime(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            localNow.Hour,
            localNow.Minute,
            0,
            DateTimeKind.Unspecified);

        for (var minute = 0; minute < 366 * 24 * 60; minute++)
        {
            var localCandidate = start.AddMinutes(minute).AddSeconds(seconds);
            if (!Matches(fields[1], localCandidate.Minute)
                || !Matches(fields[2], localCandidate.Hour)
                || !Matches(fields[3], localCandidate.Day)
                || !Matches(fields[4], localCandidate.Month)
                || !Matches(fields[5], (int)localCandidate.DayOfWeek)
                || ChinaTimeZone.IsInvalidTime(localCandidate))
                continue;

            TimeSpan[] offsets = ChinaTimeZone.IsAmbiguousTime(localCandidate)
                ? ChinaTimeZone.GetAmbiguousTimeOffsets(localCandidate)
                : [ChinaTimeZone.GetUtcOffset(localCandidate)];
            foreach (var occurrence in offsets
                .Select(offset => new DateTimeOffset(localCandidate, offset))
                .Where(candidate => candidate > now)
                .OrderBy(candidate => candidate.UtcDateTime))
                return occurrence;
        }

        throw new FormatException("cron 在一年内没有可执行时间");
    }

    private static TimeZoneInfo ResolveChinaTimeZone()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "Asia/Shanghai",
            TimeSpan.FromHours(8),
            "China Standard Time",
            "China Standard Time");
    }

    private static int ParseNumber(string value, string field, int minimum, int maximum)
    {
        if (!int.TryParse(value, out var number) || number < minimum || number > maximum)
            throw new FormatException($"cron {field} 字段无效");
        return number;
    }

    private static bool Matches(string expression, int value)
    {
        foreach (var part in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part is "*" or "?") return true;
            if (part.StartsWith("*/", StringComparison.Ordinal)
                && int.TryParse(part[2..], out var step)
                && step > 0
                && value % step == 0)
                return true;
            if (int.TryParse(part, out var exact) && exact == value)
                return true;
        }

        return false;
    }
}
