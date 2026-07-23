using Hope.Desktop.Ipc;
using Hope.Desktop.Services;
using Xunit;

namespace Hope.Desktop.Tests;

public class ProgressBarRefreshTests
{
    private static TaskDto Instant(string id, DateTimeOffset created, DateTimeOffset end, bool completed = false) => new()
    {
        Id = id,
        Name = id,
        Type = "instant",
        CreatedAt = created,
        StartTs = created.ToUnixTimeSeconds(),
        EndTs = end.ToUnixTimeSeconds(),
        Completed = completed,
    };

    [Fact]
    public void ActiveInstant_ShouldReset()
    {
        var now = DateTimeOffset.Parse("2026-07-12T12:00:00+08:00");
        var t = Instant("a", now.AddHours(-1), now.AddHours(2));
        Assert.True(ProgressBarRefresh.ShouldResetInstantStart(t, now));
    }

    [Fact]
    public void ExpiredInstant_ShouldNotReset()
    {
        var now = DateTimeOffset.Parse("2026-07-12T12:00:00+08:00");
        var t = Instant("a", now.AddHours(-2), now.AddMinutes(-1));
        Assert.False(ProgressBarRefresh.ShouldResetInstantStart(t, now));
    }

    [Fact]
    public void CompletedInstant_ShouldNotReset()
    {
        var now = DateTimeOffset.Parse("2026-07-12T12:00:00+08:00");
        var t = Instant("a", now.AddHours(-1), now.AddHours(2), completed: true);
        Assert.False(ProgressBarRefresh.ShouldResetInstantStart(t, now));
    }

    [Fact]
    public void Scheduled_ShouldNotReset()
    {
        var now = DateTimeOffset.Parse("2026-07-12T12:00:00+08:00");
        var t = new TaskDto
        {
            Id = "s",
            Type = "scheduled",
            StartTs = now.AddHours(-1).ToUnixTimeSeconds(),
            EndTs = now.AddHours(2).ToUnixTimeSeconds(),
        };
        Assert.False(ProgressBarRefresh.ShouldResetInstantStart(t, now));
    }

    [Fact]
    public void WithInstantStartReset_KeepsDuration()
    {
        var now = DateTimeOffset.Parse("2026-07-12T12:00:00+08:00");
        // 原时长 = 4 小时（now-1h ~ now+3h）；刷新后应以 now 为起点、保持 4 小时时长。
        var t = Instant("a", now.AddHours(-1), now.AddHours(3));
        var u = ProgressBarRefresh.WithInstantStartReset(t, now);
        Assert.Equal(now.ToUnixTimeSeconds(), u.StartTs);
        Assert.Equal(now, u.CreatedAt);
        Assert.Equal(now.ToUnixTimeSeconds() + (t.EndTs - t.StartTs), u.EndTs);
        Assert.Equal(now.AddHours(4).ToUnixTimeSeconds(), u.EndTs);
        Assert.Equal("a", u.Id);
    }
}
