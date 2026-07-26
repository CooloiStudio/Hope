using Hope.Desktop;
using Xunit;

namespace Hope.Desktop.Tests;

/// <summary>
/// 相对/绝对日期展示依赖 DateTimeOffset.LocalDateTime（本机墙钟）。
/// 构造用例时必须用本机时区偏移，不能写死 +08：CI（常为 UTC）下写死东八区会变成 00:01。
/// </summary>
public sealed class TaskScheduleRelativeDateTests
{
    /// <summary>按本机时区构造墙钟时刻（年-月-日 时:分）。</summary>
    private static DateTimeOffset AtLocal(int year, int month, int day, int hour = 12, int minute = 0)
    {
        var wall = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset);
    }

    private static DateTimeOffset AtLocal(DateTimeOffset day, int hour = 12, int minute = 0) =>
        AtLocal(day.Year, day.Month, day.Day, hour, minute);

    private static readonly DateTimeOffset Anchor = AtLocal(2026, 7, 21, 12, 0);

    [Theory]
    [InlineData(-2, "前天")]
    [InlineData(-1, "昨天")]
    [InlineData(0, "今天")]
    [InlineData(1, "明天")]
    [InlineData(2, "后天")]
    public void FormatListRelative_MapsDayDiffWithinWindow(int dayOffset, string expectedLabel)
    {
        var now = AtLocal(Anchor, 9, 8);
        var value = AtLocal(Anchor.AddDays(dayOffset), 8, 1);
        var text = TaskSchedule.FormatListRelative(value, now);
        Assert.Equal($"08:01 {expectedLabel}", text);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    [InlineData(-10)]
    [InlineData(30)]
    public void FormatListRelative_OutsideWindow_ReturnsNull(int dayOffset)
    {
        var now = AtLocal(Anchor);
        var value = AtLocal(Anchor.AddDays(dayOffset), 8, 1);
        Assert.Null(TaskSchedule.FormatListRelative(value, now));
    }

    [Fact]
    public void FormatListRelative_SameClockAcrossDays_UsesCalendarDateNotElapsedHours()
    {
        // 复现：任务截止仍是 7-19 08:01；若 now 仍按打开设置窗那天算会错成「今天」。
        var value = AtLocal(2026, 7, 19, 8, 1);
        Assert.Equal("08:01 今天", TaskSchedule.FormatListRelative(value, AtLocal(2026, 7, 19, 22, 0)));
        Assert.Equal("08:01 昨天", TaskSchedule.FormatListRelative(value, AtLocal(2026, 7, 20, 9, 0)));
        Assert.Equal("08:01 前天", TaskSchedule.FormatListRelative(value, AtLocal(2026, 7, 21, 9, 0)));
        Assert.Null(TaskSchedule.FormatListRelative(value, AtLocal(2026, 7, 22, 9, 0)));
    }

    [Fact]
    public void FormatListAbsolute_UnaffectedByNow()
    {
        var value = AtLocal(2026, 7, 19, 8, 1);
        Assert.Equal("08:01 07-19", TaskSchedule.FormatListAbsolute(value));
    }

    [Theory]
    [InlineData(-2, "将于 前天 08:01 截止")]
    [InlineData(-1, "将于 昨天 08:01 截止")]
    [InlineData(0, "将于 今天 08:01 截止")]
    [InlineData(1, "将于 明天 08:01 截止")]
    [InlineData(2, "将于 后天 08:01 截止")]
    public void FormatCountdownDeadlineSummary_UsesFriendlyDayWithinWindow(int dayOffset, string expected)
    {
        var now = AtLocal(Anchor, 9, 8);
        var end = AtLocal(Anchor.AddDays(dayOffset), 8, 1);
        Assert.Equal(expected, TaskSchedule.FormatCountdownDeadlineSummary(end, now));
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    [InlineData(30)]
    public void FormatCountdownDeadlineSummary_OutsideWindow_UsesYmd(int dayOffset)
    {
        var now = AtLocal(Anchor);
        var end = AtLocal(Anchor.AddDays(dayOffset), 8, 1);
        var expected = $"将于 {end.LocalDateTime:yyyy-MM-dd HH:mm} 截止";
        Assert.Equal(expected, TaskSchedule.FormatCountdownDeadlineSummary(end, now));
    }
}
