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

    [Fact]
    public void ShouldDeferOverlayHardReset_WithinSuppressWindow_True()
    {
        Assert.True(ResumeSettlePolicy.ShouldDeferOverlayHardReset(TimeSpan.Zero));
        Assert.True(ResumeSettlePolicy.ShouldDeferOverlayHardReset(TimeSpan.FromSeconds(10)));
        Assert.True(ResumeSettlePolicy.ShouldDeferOverlayHardReset(
            TimeSpan.FromSeconds(ResumeSettlePolicy.OverlayHardResetSuppressSeconds - 0.01)));
    }

    [Fact]
    public void ShouldDeferOverlayHardReset_AfterSuppressWindow_False()
    {
        Assert.False(ResumeSettlePolicy.ShouldDeferOverlayHardReset(
            TimeSpan.FromSeconds(ResumeSettlePolicy.OverlayHardResetSuppressSeconds)));
        Assert.False(ResumeSettlePolicy.ShouldDeferOverlayHardReset(TimeSpan.FromMinutes(1)));
        Assert.False(ResumeSettlePolicy.ShouldDeferOverlayHardReset(TimeSpan.FromSeconds(-1)));
    }
}
