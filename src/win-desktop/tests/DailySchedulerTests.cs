using Hope.Desktop.Services;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class DailySchedulerTests
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [Fact]
    public void TryFire_BeforeInterval_ReturnsFalseAndKeepsLastTick()
    {
        var start = new DateTime(2026, 7, 24, 9, 0, 0, DateTimeKind.Utc);
        var s = new DailyScheduler(start, Day);

        Assert.False(s.TryFire(start + TimeSpan.FromHours(23)));
        Assert.Equal(start, s.LastTickUtc);
    }

    [Fact]
    public void TryFire_AtOrAfterInterval_FiresOnceAndAdvancesLastTick()
    {
        var start = new DateTime(2026, 7, 24, 9, 0, 0, DateTimeKind.Utc);
        var s = new DailyScheduler(start, Day);

        var wake = start + TimeSpan.FromHours(24);
        Assert.True(s.TryFire(wake));
        Assert.Equal(wake, s.LastTickUtc);

        // 同一时刻再次调用不重复触发（须再等满一个周期）。
        Assert.False(s.TryFire(wake));
    }

    // 回归核心：旧的 1 天 DispatcherTimer 每次唤醒被 Stop/Start 清零 → 每天休眠的机器永不到点。
    // 墙钟判定下，休眠 25h 后唤醒的第一次检查即应触发当日单元。
    [Fact]
    public void TryFire_AfterOvernightSleep_FiresOnWake()
    {
        var launch = new DateTime(2026, 7, 24, 9, 39, 0, DateTimeKind.Utc);
        var s = new DailyScheduler(launch, Day);

        // 期间机器休眠，无任何轮询触发；次日唤醒（>24h）后首次判定。
        var nextWake = launch + TimeSpan.FromHours(25);
        Assert.True(s.TryFire(nextWake));
        Assert.Equal(nextWake, s.LastTickUtc);
    }

    // 跨多个周期（连睡数日）唤醒时也只补发一次，不做补火风暴。
    [Fact]
    public void TryFire_AfterMultipleMissedPeriods_FiresOnlyOncePerWake()
    {
        var start = new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc);
        var s = new DailyScheduler(start, Day);

        var wake = start + TimeSpan.FromDays(3);
        Assert.True(s.TryFire(wake));
        Assert.False(s.TryFire(wake));                       // 立即再调不重复
        Assert.False(s.TryFire(wake + TimeSpan.FromHours(23))); // 未满下一周期
        Assert.True(s.TryFire(wake + TimeSpan.FromDays(1)));    // 满一周期再触发
    }
}
