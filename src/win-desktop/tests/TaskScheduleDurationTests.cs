using System;
using Hope.Desktop;
using Xunit;

namespace Hope.Desktop.Tests;

public class TaskScheduleDurationTests
{
    private static DateTime D(int y, int mo, int d, int h = 0, int mi = 0) =>
        new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Local);

    [Fact]
    public void SubDay_ShowsHourAndMinute()
    {
        // 8 小时 30 分：不满一天不显示天。
        Assert.Equal("8小时30分", TaskSchedule.FormatDurationBetween(D(2026, 5, 1, 9, 0), D(2026, 5, 1, 17, 30)));
    }

    [Fact]
    public void MultiDay_KeepsIntermediateZeroHour()
    {
        // 2 天 0 小时 30 分：中间的 0 小时保留，不满一周不显示周。
        Assert.Equal("2天0小时30分", TaskSchedule.FormatDurationBetween(D(2026, 5, 1, 9, 0), D(2026, 5, 3, 9, 30)));
    }

    [Fact]
    public void ExactDays_TrimsTrailingZeros()
    {
        // 恰好整天：去掉尾部 0 小时 0 分。
        Assert.Equal("3天", TaskSchedule.FormatDurationBetween(D(2026, 5, 1, 9, 0), D(2026, 5, 4, 9, 0)));
    }

    [Fact]
    public void EndOfMonthSpan_DowngradesToDays()
    {
        // 4/30 → 5/31：区间未完整包含任何自然月，降级为「31天」（不含月）。
        Assert.Equal("31天", TaskSchedule.FormatDurationBetween(D(2026, 4, 30), D(2026, 5, 31)));
    }

    [Fact]
    public void SpanContainingFullMonth_ShowsMonth()
    {
        // 1/31 → 3/3：完整包含 2 月，展示「1月3天」（起点锚定日历拆分）。
        Assert.Equal("1月3天", TaskSchedule.FormatDurationBetween(D(2026, 1, 31), D(2026, 3, 3)));
    }

    [Fact]
    public void OverAYear_ShowsYear()
    {
        // 跨年：展示「年」。
        Assert.Equal("1年", TaskSchedule.FormatDurationBetween(D(2026, 1, 1), D(2027, 1, 1)));
    }

    [Fact]
    public void InvalidRange_ReturnsEmpty()
    {
        Assert.Equal("", TaskSchedule.FormatDurationLabel(0, 100));
        Assert.Equal("", TaskSchedule.FormatDurationLabel(200, 100));
    }

    [Fact]
    public void SplitAndCompose_RoundTrips()
    {
        long secs = TaskSchedule.ComposeDaysHoursMinutes(2, 3, 30);
        Assert.Equal(2L * 86400 + 3 * 3600 + 30 * 60, secs);
        var (days, hours, minutes) = TaskSchedule.SplitDaysHoursMinutes(secs);
        Assert.Equal(2L, days);
        Assert.Equal(3, hours);
        Assert.Equal(30, minutes);
    }
}
