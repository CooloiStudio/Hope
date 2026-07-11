using Hope.Desktop.Ipc;
using Hope.Desktop.Overlay;
using Xunit;

namespace Hope.Desktop.Tests;

public class BlinkHoverResolverTests
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Segment Seg(
        string id, string name, string color,
        double barStart, double fillEnd,
        bool expired = false, string? behavior = null) => new()
    {
        TaskId = id,
        Name = name,
        Color = color,
        BarStart = barStart,
        FillEnd = fillEnd,
        BarEnd = fillEnd,
        Percent = fillEnd,
        Expired = expired,
        Behaviors = behavior == null ? null : new List<string> { behavior },
        EndAt = Anchor,
    };

    [Fact]
    public void ActiveBand_UsesGeometricHit_NotBlinkPhase()
    {
        var segs = new List<Segment>
        {
            Seg("a", "进行中", "#111111", 0, 40),
            Seg("b", "到期甲", "#FF0000", 40, 100, expired: true, behavior: "blink"),
            Seg("c", "到期乙", "#00FF00", 40, 100, expired: true, behavior: "blink"),
        };

        var hit = BlinkHoverResolver.Resolve(segs, pct: 20, utcNow: Anchor, blinkAnchorUtc: Anchor);
        Assert.NotNull(hit);
        Assert.Equal("进行中", hit!.Name);
    }

    [Fact]
    public void BlinkOverlap_AtPhaseZero_ShowsFirstTaskId()
    {
        var segs = new List<Segment>
        {
            Seg("b", "任务B", "#00FF00", 0, 100, expired: true, behavior: "blink"),
            Seg("a", "任务A", "#FF0000", 0, 100, expired: true, behavior: "blink"),
        };

        var hit = BlinkHoverResolver.Resolve(segs, pct: 50, utcNow: Anchor, blinkAnchorUtc: Anchor);
        Assert.NotNull(hit);
        Assert.Equal("a", hit!.TaskId);
        Assert.Equal("任务A", hit.Name);
    }

    [Fact]
    public void BlinkOverlap_AfterOneStep_ShowsSecondTask()
    {
        var segs = new List<Segment>
        {
            Seg("a", "任务A", "#FF0000", 0, 100, expired: true, behavior: "blink"),
            Seg("b", "任务B", "#00FF00", 0, 100, expired: true, behavior: "blink"),
        };
        // 两色步长 = Clamp(4/2, 1, 1.5) = 1.5s
        var now = Anchor.AddSeconds(1.5);

        var hit = BlinkHoverResolver.Resolve(segs, pct: 50, utcNow: now, blinkAnchorUtc: Anchor);
        Assert.NotNull(hit);
        Assert.Equal("b", hit!.TaskId);
    }

    [Fact]
    public void SingleBlink_AlwaysSameTask_AcrossDimPhase()
    {
        var segs = new List<Segment>
        {
            Seg("only", "唯一", "#112233", 0, 100, expired: true, behavior: "blink"),
        };

        var at0 = BlinkHoverResolver.Resolve(segs, 50, Anchor, Anchor);
        var atDim = BlinkHoverResolver.Resolve(segs, 50, Anchor.AddSeconds(1.5), Anchor);
        Assert.Equal("唯一", at0!.Name);
        Assert.Equal("唯一", atDim!.Name);
    }

    [Fact]
    public void SameColor_DedupesToOnePaletteEntry()
    {
        var palette = BlinkHoverResolver.BuildPalette(new[]
        {
            Seg("a", "甲", "#FF0000", 0, 100, expired: true, behavior: "blink"),
            Seg("b", "乙", "#FF0000", 0, 100, expired: true, behavior: "blink"),
        });
        Assert.Single(palette);
        Assert.Equal("a", palette[0].TaskId);
    }

    [Fact]
    public void CelebrateBehavior_IncludedInBlinkPalette()
    {
        var segs = new List<Segment>
        {
            Seg("a", "庆A", "#FF0000", 0, 100, expired: true, behavior: "celebrate"),
            Seg("b", "庆B", "#00FF00", 0, 100, expired: true, behavior: "celebrate"),
        };
        var hit = BlinkHoverResolver.Resolve(segs, 50, Anchor.AddSeconds(1.5), Anchor);
        Assert.Equal("b", hit!.TaskId);
    }
}
