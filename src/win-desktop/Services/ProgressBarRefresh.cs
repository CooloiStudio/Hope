using Hope.Desktop.Ipc;

namespace Hope.Desktop.Services;

/// <summary>
/// 「刷新进度条」手动路径：判定哪些即时任务应把起点重置为点击时刻。
/// </summary>
public static class ProgressBarRefresh
{
    /// <summary>
    /// 进行中的即时任务：未完成、已开始、未到期，且用 now 作起点后仍早于截止。
    /// </summary>
    public static bool ShouldResetInstantStart(TaskDto task, DateTimeOffset now)
    {
        if (!string.Equals(task.Type, "instant", StringComparison.OrdinalIgnoreCase))
            return false;
        if (task.Completed || string.Equals(task.Status, "completed", StringComparison.OrdinalIgnoreCase))
            return false;

        var (startTs, endTs) = TaskSchedule.ResolveTimestamps(task);
        if (endTs <= 0) return false;

        var nowTs = now.ToUnixTimeSeconds();
        if (nowTs >= endTs) return false; // 已到期
        if (!TaskSchedule.HasStarted(task.Type, startTs, endTs, task.CreatedAt, now))
            return false;
        // 重置后起点必须仍早于截止
        return nowTs < endTs;
    }

    /// <summary>
    /// 生成用于 updateTask 的副本：以 now 为新起点，并保持原时长（end = now + (原 end − 原 start)）。
    /// 仅重置进度、不缩短倒计时时长（旧行为「只改 start、end 不变」会缩短剩余时间）。
    /// </summary>
    public static TaskDto WithInstantStartReset(TaskDto task, DateTimeOffset now)
    {
        var (startTs, endTs) = TaskSchedule.ResolveTimestamps(task);
        long dur = endTs - startTs;
        if (dur < 0) dur = 0;
        long newStart = now.ToUnixTimeSeconds();
        return new TaskDto
        {
            Id = task.Id,
            Name = task.Name,
            Type = task.Type,
            Color = task.Color,
            Gif = task.Gif,
            ImageMaxSize = task.ImageMaxSize,
            StartTs = newStart,
            EndTs = newStart + dur,
            CreatedAt = now,
            Status = task.Status,
            Completed = task.Completed,
            CompletedAt = task.CompletedAt,
            Position = task.Position,
            ExpiredBehaviors = task.ExpiredBehaviors,
            Recurrence = task.Recurrence,
        };
    }
}
