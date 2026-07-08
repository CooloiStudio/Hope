using Hope.Desktop.Ipc;
using Hope.Desktop.Views;

namespace Hope.Desktop;

/// <summary>
/// 任务时间戳与进度计算（与 headless task 包语义对齐，供列表与托盘展示）。
/// 业务逻辑仅使用 Unix 秒比较与四则运算；日期时间仅用于展示。
/// </summary>
internal static class TaskSchedule
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

    public static DateTimeOffset EffectiveEndDisplay(string type, long startTs, long endTs, DateTimeOffset? createdAt) =>
        DateTimeOffset.FromUnixTimeSeconds(EffectiveEndTs(endTs)).ToLocalTime();

    /// <summary>列表进度列：已完成 / 未开始 / 已到期 / 百分比。</summary>
    public static string GetListProgressLabel(TaskRow row, DateTimeOffset now)
    {
        if (row.Completed) return "已完成";
        return GetActiveProgressLabel(row.Type, row.StartTs, row.EndTs, row.CreatedAt, now);
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
        return $"{Math.Round(pct):0}%";
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
