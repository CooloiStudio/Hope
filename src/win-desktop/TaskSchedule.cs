using Hope.Desktop.Ipc;
using Hope.Desktop.Views;

namespace Hope.Desktop;

/// <summary>
/// 任务时间窗与进度计算（与 headless task 包语义对齐，供列表与托盘展示）。
/// </summary>
internal static class TaskSchedule
{
    /// <summary>列表进度列：已完成 / 未开始 / 已到期 / 百分比。</summary>
    public static string GetListProgressLabel(TaskRow row, DateTimeOffset now)
    {
        if (row.Completed) return "已完成";
        return GetActiveProgressLabel(row.Type, row.StartAt, row.EndAt, row.CreatedAt, row.Recurrence, now);
    }

    /// <summary>托盘状态列：未开始 / 已到期 / 倒计时。</summary>
    public static string GetTrayStatusLabel(TaskRow row, DateTimeOffset now)
    {
        if (row.Completed) return "已完成";
        if (!HasStarted(row.Type, row.StartAt, row.EndAt, row.CreatedAt, row.Recurrence, now))
            return "未开始";
        if (IsExpired(row.Type, row.StartAt, row.EndAt, row.CreatedAt, row.Recurrence, now))
            return "已到期";
        return FormatCountdown(EffectiveEnd(row.Type, row.StartAt, row.EndAt, row.CreatedAt, row.Recurrence, now), now);
    }

    public static string GetActiveProgressLabel(string type, DateTimeOffset? startAt, DateTimeOffset endAt,
        DateTimeOffset? createdAt, RecurrenceDto? recurrence, DateTimeOffset now)
    {
        if (!HasStarted(type, startAt, endAt, createdAt, recurrence, now))
            return "未开始";
        if (IsExpired(type, startAt, endAt, createdAt, recurrence, now))
            return "已到期";
        var pct = Percent(type, startAt, endAt, createdAt, recurrence, now);
        return $"{Math.Round(pct):0}%";
    }

    public static bool HasStarted(string type, DateTimeOffset? startAt, DateTimeOffset endAt,
        DateTimeOffset? createdAt, RecurrenceDto? recurrence, DateTimeOffset now) =>
        WindowAt(type, startAt, endAt, createdAt, recurrence, now).Started;

    public static bool IsExpired(string type, DateTimeOffset? startAt, DateTimeOffset endAt,
        DateTimeOffset? createdAt, RecurrenceDto? recurrence, DateTimeOffset now)
    {
        var (_, end, started) = WindowAt(type, startAt, endAt, createdAt, recurrence, now);
        return started && now >= end;
    }

    public static double Percent(string type, DateTimeOffset? startAt, DateTimeOffset endAt,
        DateTimeOffset? createdAt, RecurrenceDto? recurrence, DateTimeOffset now)
    {
        var (start, end, started) = WindowAt(type, startAt, endAt, createdAt, recurrence, now);
        if (!started) return 0;
        var total = end - start;
        if (total <= TimeSpan.Zero) return 100;
        var p = (now - start).TotalMilliseconds / total.TotalMilliseconds * 100;
        return Math.Clamp(p, 0, 100);
    }

    public static DateTimeOffset EffectiveEnd(string type, DateTimeOffset? startAt, DateTimeOffset endAt,
        DateTimeOffset? createdAt, RecurrenceDto? recurrence, DateTimeOffset now)
    {
        var (_, end, started) = WindowAt(type, startAt, endAt, createdAt, recurrence, now);
        return started ? end : endAt;
    }

    private static (DateTimeOffset Start, DateTimeOffset End, bool Started) WindowAt(
        string type, DateTimeOffset? startAt, DateTimeOffset endAt,
        DateTimeOffset? createdAt, RecurrenceDto? recurrence, DateTimeOffset now)
    {
        if (!IsRecurring(type, startAt, recurrence))
        {
            var start = EffectiveStart(type, startAt, createdAt);
            if (now < start) return (start, endAt, false);
            return (start, endAt, true);
        }

        var startRef = startAt!.Value;
        var startTod = startRef.TimeOfDay;
        var endTod = endAt.TimeOfDay;
        var anchor = DateOnly.FromDateTime(startRef.DateTime);
        var d0 = DateOnly.FromDateTime(now.DateTime);

        int limit = 8;
        if (recurrence!.Mode == "everyN" && recurrence.Interval + 1 > limit)
            limit = recurrence.Interval + 1;
        if (limit > 400) limit = 400;

        for (int i = 0; i <= limit; i++)
        {
            var d = d0.AddDays(-i);
            if (d < anchor) break;
            if (!IsOccurrenceDay(recurrence, anchor, d)) continue;

            var ws = Combine(d, startTod, startRef.Offset);
            var we = Combine(d, endTod, endAt.Offset);
            if (endTod <= startTod)
                we = Combine(d.AddDays(1), endTod, endAt.Offset);

            if (i == 0 && now < ws)
            {
                if (endTod <= startTod)
                {
                    var yd = d.AddDays(-1);
                    if (yd >= anchor && IsOccurrenceDay(recurrence, anchor, yd))
                    {
                        var yws = Combine(yd, startTod, startRef.Offset);
                        var ywe = Combine(d, endTod, endAt.Offset);
                        if (now >= yws) return (yws, ywe, true);
                    }
                }
                return (ws, we, false);
            }

            if (ws <= now) return (ws, we, true);
        }

        return (default, default, false);
    }

    private static DateTimeOffset Combine(DateOnly date, TimeSpan tod, TimeSpan offset)
    {
        var dt = date.ToDateTime(TimeOnly.FromTimeSpan(tod));
        return new DateTimeOffset(dt, offset);
    }

    private static DateTimeOffset EffectiveStart(string type, DateTimeOffset? startAt, DateTimeOffset? createdAt) =>
        type == "scheduled" && startAt.HasValue ? startAt.Value : (createdAt ?? DateTimeOffset.Now);

    private static bool IsRecurring(string type, DateTimeOffset? startAt, RecurrenceDto? recurrence) =>
        type == "scheduled" && startAt.HasValue &&
        recurrence != null && !string.IsNullOrEmpty(recurrence.Mode);

    private static bool IsOccurrenceDay(RecurrenceDto rec, DateOnly anchor, DateOnly d)
    {
        if (d < anchor) return false;
        switch (rec.Mode)
        {
            case "daily":
                return true;
            case "everyN":
            {
                int n = rec.Interval < 1 ? 1 : rec.Interval;
                int days = (int)Math.Round((d.ToDateTime(TimeOnly.MinValue) - anchor.ToDateTime(TimeOnly.MinValue)).TotalDays);
                return days % n == 0;
            }
            case "weekly":
                return rec.Weekdays != null && rec.Weekdays.Contains((int)d.DayOfWeek);
            default:
                return false;
        }
    }

    private static string FormatCountdown(DateTimeOffset endAt, DateTimeOffset now)
    {
        var remaining = endAt - now;
        if (remaining <= TimeSpan.Zero) return "已到期";
        return remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}天 {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    /// <summary>列表绝对日期：HH:mm MM-dd。</summary>
    public static string FormatListAbsolute(DateTimeOffset value) =>
        value.LocalDateTime.ToString("HH:mm MM-dd");

    /// <summary>列表相对日期（前天~后天）：如「08:01 今天」；超出范围返回 null。</summary>
    public static string? FormatListRelative(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.LocalDateTime;
        var dayDiff = (local.Date - now.LocalDateTime.Date).Days;
        var relative = dayDiff switch
        {
            -2 => "前天",
            -1 => "昨天",
            0 => "今天",
            1 => "明天",
            2 => "后天",
            _ => null,
        };
        if (relative == null) return null;
        return $"{local:HH:mm} {relative}";
    }
}
