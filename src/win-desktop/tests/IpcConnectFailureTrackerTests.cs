using Hope.Desktop.Ipc;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class IpcConnectFailureTrackerTests
{
    [Fact]
    public void TryReportFatal_OnlyOnceAfterExceedingMax()
    {
        var tracker = new IpcConnectFailureTracker { MaxConnectFailures = 2 };
        Assert.False(tracker.TryReportFatal(out _)); // 1
        Assert.False(tracker.TryReportFatal(out _)); // 2
        Assert.True(tracker.TryReportFatal(out var reason)); // 3 > 2
        Assert.Contains("连续失败", reason);
        Assert.False(tracker.TryReportFatal(out _)); // 已报过
        Assert.True(tracker.FatalReported);
    }

    [Fact]
    public void OnConnected_ResetsFailureAndAllowsFatalAgain()
    {
        var tracker = new IpcConnectFailureTracker { MaxConnectFailures = 1 };
        Assert.False(tracker.TryReportFatal(out _));
        Assert.True(tracker.TryReportFatal(out _));

        tracker.OnConnected();
        Assert.False(tracker.FatalReported);
        Assert.Equal(0, tracker.ConsecutiveFailures);
        Assert.False(tracker.TryReportFatal(out _));
        Assert.True(tracker.TryReportFatal(out _));
    }
}
