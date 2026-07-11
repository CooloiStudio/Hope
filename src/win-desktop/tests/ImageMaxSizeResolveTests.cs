using Hope.Desktop.State;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class ImageMaxSizeResolveTests
{
    [Theory]
    [InlineData(false, 0, 20, 20)]
    [InlineData(false, 30, 20, 20)] // 未开启任务级：忽略任务值
    [InlineData(true, 0, 20, 20)]
    [InlineData(true, 25, 20, 25)]
    [InlineData(true, 99, 20, 30)] // 钳制上限
    [InlineData(true, 5, 20, 15)]  // 钳制下限（>0 才走任务值）
    [InlineData(false, 0, 0, 15)]  // 全局缺省
    public void ResolveImageMaxSize_Matrix(bool advanced, int taskSize, int globalPx, int want)
    {
        Assert.Equal(want, SessionState.ResolveImageMaxSize(advanced, taskSize, globalPx));
    }

    [Fact]
    public void Session_AppliesAdvancedImageHeightFlag()
    {
        var session = new SessionState();
        session.ApplySettings(new Hope.Desktop.Ipc.SettingsDto
        {
            AdvancedImageHeight = true,
            ImageMaxHeightPx = 18,
        });
        Assert.True(session.OverlayAdvancedImageHeight);
        Assert.Equal(18, session.OverlayImageMaxHeightPx);
        Assert.Equal(28, session.ResolveImageMaxSize(28));
        Assert.Equal(18, session.ResolveImageMaxSize(0));
    }
}
