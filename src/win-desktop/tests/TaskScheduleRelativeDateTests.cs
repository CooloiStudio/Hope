using Hope.Desktop;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class TaskScheduleRelativeDateTests
{
    private static DateTimeOffset AtLocal(DateTimeOffset day, int hour = 12, int minute = 0) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, day.Offset);

    private static readonly DateTimeOffset Anchor = new(2026, 7, 21, 12, 0, 0, TimeSpan.FromHours(8));

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
        var value = AtLocal(new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.FromHours(8)), 8, 1);
        Assert.Equal("08:01 今天", TaskSchedule.FormatListRelative(value, AtLocal(new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.FromHours(8)), 22, 0)));
        Assert.Equal("08:01 昨天", TaskSchedule.FormatListRelative(value, AtLocal(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.FromHours(8)), 9, 0)));
        Assert.Equal("08:01 前天", TaskSchedule.FormatListRelative(value, AtLocal(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.FromHours(8)), 9, 0)));
        Assert.Null(TaskSchedule.FormatListRelative(value, AtLocal(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.FromHours(8)), 9, 0)));
    }

    [Fact]
    public void FormatListAbsolute_UnaffectedByNow()
    {
        var value = AtLocal(new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.FromHours(8)), 8, 1);
        Assert.Equal("08:01 07-19", TaskSchedule.FormatListAbsolute(value));
    }
}
