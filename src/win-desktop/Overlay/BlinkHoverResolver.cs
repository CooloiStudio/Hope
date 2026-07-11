using System.Windows.Media;
using Hope.Desktop.Ipc;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Hope.Desktop.Overlay;

/// <summary>
/// 呼吸轮换时的悬停文案解析：按与 Overlay 色板相同的顺序与墙钟相位，选出当前应展示的任务。
/// 仅用于 Tooltip，不驱动、不修改进度条动画。
/// 相位参数须与 <see cref="OverlayWindow"/> 内 Blink* 常量保持一致。
/// </summary>
public static class BlinkHoverResolver
{
    // 与 OverlayWindow 呼吸步长约定保持一致（勿单独改此处）。
    private const double BlinkCycleTargetSec = 4.0;
    private const double BlinkStepMinSec = 1.0;
    private const double BlinkStepMaxSec = 1.5;
    private const double BlinkSingleBreatheStepSec = 1.5;

    /// <summary>
    /// 解析悬停应展示的段：光标落在呼吸到期重叠区时按相位选任务，否则按几何 First 命中。
    /// </summary>
    public static Segment? Resolve(
        IReadOnlyList<Segment> segments,
        double pct,
        DateTime utcNow,
        DateTime blinkAnchorUtc,
        IReadOnlySet<string>? acknowledgedBlink = null)
    {
        var geometric = segments.FirstOrDefault(s => pct >= s.BarStart && pct <= s.FillEnd);
        if (geometric == null) return null;

        var blinkSegs = segments
            .Where(s => IsBlinkPaletteSegment(s) && (acknowledgedBlink == null || !acknowledgedBlink.Contains(s.TaskId)))
            .ToList();
        if (blinkSegs.Count == 0)
            return geometric;

        bool onBlinkBand = blinkSegs.Any(s => pct >= s.BarStart && pct <= s.FillEnd);
        if (!onBlinkBand)
            return geometric;

        var palette = BuildPalette(blinkSegs);
        if (palette.Count == 0)
            return geometric;

        return ResolveFromPalette(palette, utcNow, blinkAnchorUtc);
    }

    /// <summary>色板：taskId 升序后按颜色去重（与 Overlay 取色顺序一致）。</summary>
    public static List<Segment> BuildPalette(IEnumerable<Segment> blinkSegments)
    {
        var ordered = blinkSegments
            .OrderBy(s => s.TaskId, StringComparer.Ordinal)
            .ToList();
        var palette = new List<Segment>();
        foreach (var s in ordered)
        {
            var c = ParseColor(s.Color);
            if (palette.Any(p => ParseColor(p.Color) == c)) continue;
            palette.Add(s);
        }
        return palette;
    }

    /// <summary>按墙钟相位从色板中取当前任务（多色硬切下标；单色始终唯一任务）。</summary>
    public static Segment ResolveFromPalette(
        IReadOnlyList<Segment> palette,
        DateTime utcNow,
        DateTime blinkAnchorUtc)
    {
        if (palette.Count == 0)
            throw new ArgumentException("palette must not be empty", nameof(palette));
        if (palette.Count == 1)
            return palette[0];

        double stepSec = BlinkStepSeconds(palette.Count);
        double total = palette.Count * stepSec;
        double elapsed = (utcNow - blinkAnchorUtc).TotalSeconds;
        double phase = (elapsed % total + total) % total;
        int idx = (int)Math.Floor(phase / stepSec) % palette.Count;
        return palette[idx];
    }

    private static bool IsBlinkPaletteSegment(Segment seg) =>
        seg.Expired
        && seg.Behaviors != null
        && (seg.Behaviors.Contains("blink") || seg.Behaviors.Contains("celebrate"));

    private static double BlinkStepSeconds(int expiredColorCount)
    {
        if (expiredColorCount <= 1)
            return BlinkSingleBreatheStepSec;
        return Math.Clamp(BlinkCycleTargetSec / expiredColorCount, BlinkStepMinSec, BlinkStepMaxSec);
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0xFF, 0x6B, 0x35); }
    }
}
