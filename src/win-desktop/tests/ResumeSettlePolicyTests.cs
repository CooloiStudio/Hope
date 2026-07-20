using Hope.Desktop;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class ResumeSettlePolicyTests
{
    [Fact]
    public void ShouldForceSettleDespiteFullscreen_BeforeCap_ReturnsFalse()
    {
        for (var i = 0; i < ResumeSettlePolicy.MaxFullscreenDefers; i++)
            Assert.False(ResumeSettlePolicy.ShouldForceSettleDespiteFullscreen(i));
    }

    [Fact]
    public void ShouldForceSettleDespiteFullscreen_AtOrAboveCap_ReturnsTrue()
    {
        Assert.True(ResumeSettlePolicy.ShouldForceSettleDespiteFullscreen(
            ResumeSettlePolicy.MaxFullscreenDefers));
        Assert.True(ResumeSettlePolicy.ShouldForceSettleDespiteFullscreen(
            ResumeSettlePolicy.MaxFullscreenDefers + 3));
    }
}
