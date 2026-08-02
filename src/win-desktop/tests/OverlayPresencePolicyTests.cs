using Hope.Desktop;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class OverlayPresencePolicyTests
{
    private static OverlayWindowFacts Healthy() =>
        new(HandleAlive: true, SystemVisible: true, Topmost: true, Layered: true, HasArea: true);

    [Fact]
    public void NeedsRebuild_HealthyWindow_False()
        => Assert.False(OverlayPresencePolicy.NeedsRebuild(wpfExpectsVisible: true, Healthy()));

    [Fact]
    public void NeedsRebuild_WhenWpfIntentionallyHidden_False()
    {
        // 无分段时 WPF 会主动 Hide，系统不可见是正常的，不能据此重建。
        var hidden = Healthy() with { SystemVisible = false, Topmost = false };
        Assert.False(OverlayPresencePolicy.NeedsRebuild(wpfExpectsVisible: false, hidden));
    }

    [Theory]
    [InlineData(false, true, true, true, true)]   // 句柄失效
    [InlineData(true, false, true, true, true)]   // 系统侧不可见
    [InlineData(true, true, false, true, true)]   // 掉出置顶层
    [InlineData(true, true, true, false, true)]   // 分层样式丢失
    [InlineData(true, true, true, true, false)]   // 窗口矩形为空
    public void NeedsRebuild_AnyBrokenFact_True(
        bool alive, bool sysVisible, bool topmost, bool layered, bool hasArea)
    {
        var facts = new OverlayWindowFacts(alive, sysVisible, topmost, layered, hasArea);
        Assert.True(OverlayPresencePolicy.NeedsRebuild(wpfExpectsVisible: true, facts));
    }

    [Fact]
    public void NeedsTopmostRepair_OnlyTopmostLost_True()
    {
        var facts = Healthy() with { Topmost = false };
        Assert.True(OverlayPresencePolicy.NeedsTopmostRepair(wpfExpectsVisible: true, facts));
    }

    [Fact]
    public void NeedsTopmostRepair_HealthyOrHidden_False()
    {
        Assert.False(OverlayPresencePolicy.NeedsTopmostRepair(wpfExpectsVisible: true, Healthy()));
        Assert.False(OverlayPresencePolicy.NeedsTopmostRepair(
            wpfExpectsVisible: false, Healthy() with { Topmost = false }));
    }

    [Fact]
    public void ShouldBackOffTopmostRepair_OnlyAtOrAboveCap()
    {
        for (var i = 0; i < OverlayPresencePolicy.MaxConsecutiveTopmostRepairs; i++)
            Assert.False(OverlayPresencePolicy.ShouldBackOffTopmostRepair(i));

        Assert.True(OverlayPresencePolicy.ShouldBackOffTopmostRepair(
            OverlayPresencePolicy.MaxConsecutiveTopmostRepairs));
    }

    [Fact]
    public void NeedsTopmostRepair_DeadHandle_FallsThroughToRebuild()
    {
        // 句柄已失效时补置顶没有意义，应交给重建路径处理。
        var dead = Healthy() with { HandleAlive = false, Topmost = false };
        Assert.False(OverlayPresencePolicy.NeedsTopmostRepair(wpfExpectsVisible: true, dead));
        Assert.True(OverlayPresencePolicy.NeedsRebuild(wpfExpectsVisible: true, dead));
    }
}
