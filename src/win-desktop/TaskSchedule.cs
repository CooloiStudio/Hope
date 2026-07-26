using Hope.Desktop.Ipc;
using Hope.Desktop.Views;

namespace Hope.Desktop;

/// <summary>
/// 任务时间戳与进度计算（与 headless task 包语义对齐，供列表与托盘展示）。
/// 业务逻辑仅使用 Unix 秒比较与四则运算；日期时间仅用于展示。
/// </summary>
public static class TaskSchedule
{
    public static long EffectiveStartTs(string type, long startTs, long endTs, DateTimeOffset? createdAt)
    {
        if (type == "scheduled" && startTs > 0) return startTs;
        if (createdAt.HasValue) return createdAt.Value.ToUnixTimeSeconds();
        return startTs > 0 ? startTs : DateTimeOffset.Now.ToUnixTimeSeconds();
    }

    public static long EffectiveEndTs(long endTs) => endTs;

    public static bool HasStarted(string type, long startTs, long endTs, DateTimeOffset? createdAt, DateTimeOffset now)
    {
        var nowTs = now.ToUnixTimeSeconds();
        return nowTs >= EffectiveStartTs(type, startTs, endTs, createdAt);
    }

    public static bool IsExpired(string type, long startTs, long endTs, DateTimeOffset? createdAt, DateTimeOffset now)
    {
        var nowTs = now.ToUnixTimeSeconds();
        var start = EffectiveStartTs(type, startTs, endTs, createdAt);
        if (nowTs < start) return false;
        return nowTs >= EffectiveEndTs(endTs);
    }

    public static double Percent(string type, long startTs, long endTs, DateTimeOffset? createdAt, DateTimeOffset now)
    {
        var nowTs = now.ToUnixTimeSeconds();
        var start = EffectiveStartTs(type, startTs, endTs, createdAt);
        var end = EffectiveEndTs(endTs);
        if (nowTs < start) return 0;
        var total = end - start;
        if (total <= 0) return 100;
        var p = (nowTs - start) * 100.0 / total;
        return Math.Clamp(p, 0, 100);
    }

    /// <summary>
    /// 列表百分比展示：向下取整到 1 位小数；未截止时封顶 99.9%，避免未到期却显示 100%。
    /// </summary>
    public static string FormatListPercent(double percent, bool expired)
    {
        if (expired) return "100.0%";
        var floored = Math.Floor(percent * 10.0) / 10.0;
        if (floored >= 100.0) floored = 99.9;
        return floored.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    public static DateTimeOffset EffectiveEndDisplay(string type, long startTs, long endTs, DateTimeOffset? createdAt) =>
        DateTimeOffset.FromUnixTimeSeconds(EffectiveEndTs(endTs)).ToLocalTime();

    /// <summary>列表进度列主文案：已完成 / 未开始 / 已到期 / 百分比。</summary>
    public static string GetListProgressLabel(TaskRow row, DateTimeOffset now)
    {
        if (row.Completed) return "已完成";
        return GetActiveProgressLabel(row.Type, row.StartTs, row.EndTs, row.CreatedAt, now);
    }

    /// <summary>
    /// 列表进度列副文案（与百分比交替）：进行中为「剩余/总时长」；其余状态返回 null（不渐变）。
    /// </summary>
    public static string? GetListProgressSpanLabel(TaskRow row, DateTimeOffset now)
    {
        if (row.Completed) return null;
        if (!HasStarted(row.Type, row.StartTs, row.EndTs, row.CreatedAt, now)) return null;
        if (IsExpired(row.Type, row.StartTs, row.EndTs, row.CreatedAt, now)) return null;

        var start = EffectiveStartTs(row.Type, row.StartTs, row.EndTs, row.CreatedAt);
        var end = EffectiveEndTs(row.EndTs);
        var total = end - start;
        if (total <= 0) return null;
        var remaining = end - now.ToUnixTimeSeconds();
        if (remaining < 0) remaining = 0;
        return $"{FormatProgressClock(remaining, total)}/{FormatProgressClock(total, total)}";
    }

    /// <summary>托盘状态列：未开始 / 已到期 / 倒计时。</summary>
    public static string GetTrayStatusLabel(TaskRow row, DateTimeOffset now)
    {
        if (row.Completed) return "已完成";
        if (!HasStarted(row.Type, row.StartTs, row.EndTs, row.CreatedAt, now))
            return "未开始";
        if (IsExpired(row.Type, row.StartTs, row.EndTs, row.CreatedAt, now))
            return "已到期";
        return FormatCountdown(EffectiveEndTs(row.EndTs), now);
    }

    public static string GetActiveProgressLabel(string type, long startTs, long endTs,
        DateTimeOffset? createdAt, DateTimeOffset now)
    {
        if (!HasStarted(type, startTs, endTs, createdAt, now))
            return "未开始";
        if (IsExpired(type, startTs, endTs, createdAt, now))
            return "已到期";
        var pct = Percent(type, startTs, endTs, createdAt, now);
        return FormatListPercent(pct, expired: false);
    }

    /// <summary>
    /// 进度列时长（精度到分，不展示秒）：总时长 ≤1 小时用分钟数（允许 60）；
    /// &lt;1 天用 HH:mm；否则「N天 HH:mm」。秒向下取整到分。
    /// <paramref name="styleTotal"/> 决定整段「剩余/总时长」的统一样式。
    /// </summary>
    public static string FormatProgressClock(long seconds, long styleTotal)
    {
        if (seconds < 0) seconds = 0;
        if (styleTotal < 0) styleTotal = 0;

        // 统一砍掉秒：整分钟向下取整（剩余 59:49 → 59 分）。
        var wholeMinutes = seconds / 60;
        var styleMinutes = styleTotal / 60;

        if (styleTotal <= 3600)
            return wholeMinutes.ToString("00");

        var days = wholeMinutes / (24 * 60);
        var remMin = wholeMinutes % (24 * 60);
        var hours = remMin / 60;
        var minutes = remMin % 60;
        if (styleTotal >= 86400 || styleMinutes >= 24 * 60 || days >= 1)
            return $"{days}天 {hours:00}:{minutes:00}";
        return $"{hours:00}:{minutes:00}";
    }

    /// <summary>由 IPC 任务解析起止戳（优先 startTs/endTs，回退旧版 startAt/endAt）。</summary>
    public static (long StartTs, long EndTs) ResolveTimestamps(TaskDto t)
    {
        long endTs = t.EndTs > 0 ? t.EndTs : t.EndAt.ToUnixTimeSeconds();
        long startTs;
        if (t.StartTs > 0)
            startTs = t.StartTs;
        else if (t.Type == "scheduled" && t.StartAt.HasValue)
            startTs = t.StartAt.Value.ToUnixTimeSeconds();
        else if (t.CreatedAt.HasValue)
            startTs = t.CreatedAt.Value.ToUnixTimeSeconds();
        else
            startTs = 0;
        while (endTs > 0 && startTs > 0 && endTs <= startTs)
            endTs += 86400;
        return (startTs, endTs);
    }

    public static DateTimeOffset? TsToLocal(long ts) =>
        ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime() : null;

    private static string FormatCountdown(long endTs, DateTimeOffset now)
    {
        var remaining = endTs - now.ToUnixTimeSeconds();
        if (remaining <= 0) return "已到期";
        var days = remaining / 86400;
        remaining %= 86400;
        var hours = remaining / 3600;
        remaining %= 3600;
        var minutes = remaining / 60;
        var seconds = remaining % 60;
        return days >= 1
            ? $"{days}天 {hours:00}:{minutes:00}:{seconds:00}"
            : $"{hours:00}:{minutes:00}:{seconds:00}";
    }

    /// <summary>列表绝对日期：HH:mm MM-dd。</summary>
    public static string FormatListAbsolute(DateTimeOffset value) =>
        value.LocalDateTime.ToString("HH:mm MM-dd");

    /// <summary>列表相对日期（前天~后天）：如「08:01 今天」；超出范围返回 null。</summary>
    public static string? FormatListRelative(DateTimeOffset value, DateTimeOffset now)
    {
        var relative = FriendlyDayLabel(value, now);
        if (relative == null) return null;
        return $"{value.LocalDateTime:HH:mm} {relative}";
    }

    /// <summary>
    /// 倒计时「按时长」只读截止文案：前天~后天用友好日名，其余用年月日。
    /// 例：「将于 今天 14:30 截止」「将于 2026-08-01 09:00 截止」。
    /// </summary>
    public static string FormatCountdownDeadlineSummary(DateTimeOffset end, DateTimeOffset now)
    {
        var local = end.LocalDateTime;
        var day = FriendlyDayLabel(end, now);
        return day != null
            ? $"将于 {day} {local:HH:mm} 截止"
            : $"将于 {local:yyyy-MM-dd HH:mm} 截止";
    }

    /// <summary>相对日历日标签（前天~后天）；超出窗口返回 null。</summary>
    public static string? FriendlyDayLabel(DateTimeOffset value, DateTimeOffset now)
    {
        var dayDiff = (value.LocalDateTime.Date - now.LocalDateTime.Date).Days;
        return dayDiff switch
        {
            -2 => "前天",
            -1 => "昨天",
            0 => "今天",
            1 => "明天",
            2 => "后天",
            _ => null,
        };
    }

    // ===== 时长展示 / 拆分（编辑区） =====

    /// <summary>
    /// 定时任务编辑区「开始 → 截止」的时长展示（起点锚定的日历时长）。
    /// startTs/endTs 无效或非正时长时返回空串。
    /// </summary>
    public static string FormatDurationLabel(long startTs, long endTs)
    {
        if (startTs <= 0 || endTs <= 0 || endTs <= startTs) return "";
        var start = DateTimeOffset.FromUnixTimeSeconds(startTs).ToLocalTime().LocalDateTime;
        var end = DateTimeOffset.FromUnixTimeSeconds(endTs).ToLocalTime().LocalDateTime;
        return FormatDurationBetween(start, end);
    }

    /// <summary>
    /// 以 <paramref name="start"/> 为锚点，把区间落到 <paramref name="end"/> 的日历日，
    /// 再按日历拆分 年/月/天/时/分（不展示「周」）。
    /// 仅当区间内完整包含至少一个自然月时才展示「月/年」，否则降级为纯天数——
    /// 例：4/30→5/31 展示「31天」（不含月）；1/31→3/3 展示「1月3天」。
    /// 展示时去掉前导与尾部的零单位、保留中间的零（如「2天0小时30分」）。
    /// </summary>
    public static string FormatDurationBetween(DateTime start, DateTime end)
    {
        if (end <= start) return "";
        int years = 0, months = 0, days, hours, minutes;
        if (ContainsFullCalendarMonth(start, end))
        {
            var cursor = start;
            while (cursor.AddYears(1) <= end) { cursor = cursor.AddYears(1); years++; }
            while (cursor.AddMonths(1) <= end) { cursor = cursor.AddMonths(1); months++; }
            var rem = end - cursor;
            days = rem.Days;
            hours = rem.Hours;
            minutes = rem.Minutes;
        }
        else
        {
            var total = end - start;
            days = (int)total.Days;
            hours = total.Hours;
            minutes = total.Minutes;
        }

        var units = new (int Value, string Suffix)[]
        {
            (years, "年"), (months, "月"), (days, "天"), (hours, "小时"), (minutes, "分"),
        };
        int first = -1, last = -1;
        for (int i = 0; i < units.Length; i++)
        {
            if (units[i].Value == 0) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return "0分"; // 不足 1 分钟

        var sb = new System.Text.StringBuilder();
        for (int i = first; i <= last; i++)
            sb.Append(units[i].Value).Append(units[i].Suffix);
        return sb.ToString();
    }

    /// <summary>区间 [start, end] 是否完整包含至少一个自然月（决定是否展示「月/年」单位）。</summary>
    internal static bool ContainsFullCalendarMonth(DateTime start, DateTime end)
    {
        var firstOfMonth = new DateTime(start.Year, start.Month, 1, 0, 0, 0, start.Kind);
        var candidate = firstOfMonth < start ? firstOfMonth.AddMonths(1) : firstOfMonth;
        return candidate.AddMonths(1) <= end;
    }

    /// <summary>把总秒数拆分为「天 / 时 / 分」（供倒计时编辑框展示；丢弃不足 1 分钟的秒）。</summary>
    public static (long Days, int Hours, int Minutes) SplitDaysHoursMinutes(long totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        long days = totalSeconds / 86400;
        long rem = totalSeconds % 86400;
        int hours = (int)(rem / 3600);
        int minutes = (int)((rem % 3600) / 60);
        return (days, hours, minutes);
    }

    /// <summary>由「天 / 时 / 分」合成总秒数（供倒计时截止时间反算）。</summary>
    public static long ComposeDaysHoursMinutes(long days, int hours, int minutes) =>
        days * 86400 + hours * 3600L + minutes * 60L;
}
