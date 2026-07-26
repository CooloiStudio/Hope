using Hope.Desktop;
using Hope.Desktop.Views;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class TaskScheduleProgressLabelTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    private static DateTimeOffset At(int y, int mo, int d, int h, int mi, int s = 0) =>
        new(y, mo, d, h, mi, s, Offset);

    private static TaskRow Active(long startTs, long endTs, string type = "scheduled") => new()
    {
        Id = "t1",
        Name = "x",
        Type = type,
        StartTs = startTs,
        EndTs = endTs,
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(startTs).ToOffset(Offset),
        Completed = false,
    };

    [Theory]
    [InlineData(99.99, false, "99.9%")]
    [InlineData(99.91, false, "99.9%")]
    [InlineData(99.90, false, "99.9%")]
    [InlineData(99.89, false, "99.8%")]
    [InlineData(0.09, false, "0.0%")]
    [InlineData(0.10, false, "0.1%")]
    [InlineData(100.0, false, "99.9%")]
    [InlineData(100.0, true, "100.0%")]
    public void FormatListPercent_FloorsToOneDecimal_AndCapsBeforeExpiry(double pct, bool expired, string expected)
    {
        Assert.Equal(expected, TaskSchedule.FormatListPercent(pct, expired));
    }

    [Fact]
    public void GetActiveProgressLabel_NearEnd_DoesNotShow100Percent()
    {
        // 总长 1000 秒，已过 999 秒 → 原始 99.9%，向下取整仍 99.9%
        var start = At(2026, 7, 26, 10, 0, 0);
        var end = start.AddSeconds(1000);
        var now = start.AddSeconds(999);
        var label = TaskSchedule.GetActiveProgressLabel(
            "scheduled", start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds(), start, now);
        Assert.Equal("99.9%", label);
    }

    [Fact]
    public void GetActiveProgressLabel_AlmostRoundedUp_StillFloors()
    {
        // 995/1000 = 99.5%，旧逻辑 Round→100%；现应 99.5%
        var start = At(2026, 7, 26, 10, 0, 0);
        var end = start.AddSeconds(1000);
        var now = start.AddSeconds(995);
        var label = TaskSchedule.GetActiveProgressLabel(
            "scheduled", start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds(), start, now);
        Assert.Equal("99.5%", label);
    }

    [Fact]
    public void GetListProgressSpanLabel_OneHour_UsesMinutesOnly()
    {
        var start = At(2026, 7, 26, 10, 0, 0);
        var end = start.AddHours(1);
        var now = start.AddSeconds(11); // 剩余 3589s → 向下取整 59 分
        var row = Active(start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
        Assert.Equal("59/60", TaskSchedule.GetListProgressSpanLabel(row, now));
    }

    [Fact]
    public void GetListProgressSpanLabel_OverOneHour_UsesHm()
    {
        var start = At(2026, 7, 26, 10, 0, 0);
        var end = start.AddHours(2);
        var now = start.AddMinutes(30);
        var row = Active(start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
        Assert.Equal("01:30/02:00", TaskSchedule.GetListProgressSpanLabel(row, now));
    }

    [Fact]
    public void GetListProgressSpanLabel_CompletedOrExpired_ReturnsNull()
    {
        var start = At(2026, 7, 26, 10, 0, 0);
        var end = start.AddHours(1);
        var row = Active(start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
        var completed = TaskRow.AsCompleted(row);
        Assert.Null(TaskSchedule.GetListProgressSpanLabel(completed, start.AddMinutes(10)));
        Assert.Null(TaskSchedule.GetListProgressSpanLabel(row, end.AddSeconds(1)));
        Assert.Null(TaskSchedule.GetListProgressSpanLabel(row, start.AddSeconds(-1)));
    }

    [Theory]
    [InlineData(0, 3600, "00")]
    [InlineData(3541, 3600, "59")]
    [InlineData(3600, 3600, "60")]
    [InlineData(3661, 7200, "01:01")]
    [InlineData(90061, 172800, "1天 01:01")]
    public void FormatProgressClock_MinutePrecision_MatchesStyleTotal(long seconds, long styleTotal, string expected)
    {
        Assert.Equal(expected, TaskSchedule.FormatProgressClock(seconds, styleTotal));
    }
}
