using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hope.Desktop.Interop;
using Hope.Desktop.Ipc;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Canvas = System.Windows.Controls.Canvas;

namespace Hope.Desktop.Overlay;

/// <summary>
/// 屏幕四边分段彩色进度条。点击穿透、不可聚焦、不参与 Alt+Tab；
/// 唯一交互为悬停展示任务名（通过全局光标轮询实现，文档 §5.4）。
/// </summary>
public partial class OverlayWindow : Window
{
    public const string PositionTop = "top";
    public const string PositionBottom = "bottom";
    public const string PositionLeft = "left";
    public const string PositionRight = "right";

    // 闪烁脉冲参数：柔和正弦渐变（淡出至近透明再淡入），单程时长 → 全周期约 2×。
    private const double BlinkHalfPeriodSec = 0.8;
    // 多个任务同时闪烁（如多任务同时到期 + 庆祝）：加长单程时长，整体更柔和。
    private const double BlinkHalfPeriodMultiSec = 1.2;
    private const double BlinkMinAlpha = 0.05;
    // 全局相位锚点（App 启动时刻，跨所有 OverlayWindow 共享）：用于让主条与三个氛围条
    // 即便在不同时刻各自启动动画，也按同一墙钟相位推进，从而保持同步。
    private static readonly DateTime BlinkAnchorUtc = DateTime.UtcNow;

    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _gifTimer;
    private readonly Dictionary<string, ImageSprite> _sprites = new();
    private List<Segment> _segments = new();
    private int _barHeightPx = 4;
    private double _imageMaxThickness;

    // 缓存图片缩放后的尺寸：(文件路径, maxSize) → (缩放后宽度, 缩放后高度)
    private readonly Dictionary<(string path, double maxSize), (double width, double height)> _imageSizeCache = new();

    public string Position { get; set; } = PositionTop;
    public string Direction { get; set; } = "forward";

    private bool IsVertical => Position is PositionLeft or PositionRight;
    private bool IsReverse => Direction == "reverse";

    // 判断某条 Segment 的本地填充方向（优先用 Segment.Direction，回退到窗口级 Direction）
    private bool IsSegReverse(Segment seg)
    {
        if (!string.IsNullOrEmpty(seg.Direction))
            return seg.Direction == "reverse";
        return IsReverse;
    }

    // 闪烁状态：当前正在脉冲的色段矩形、已查看（停止脉冲）的任务、本帧应闪烁的任务集合。
    private readonly List<Rectangle> _blinkRects = new();
    private readonly HashSet<string> _acknowledgedBlink = new();
    private readonly HashSet<string> _blinkingIds = new();

    // 方案A：闪烁矩形按 taskId 持久化复用。几何/颜色/错峰参数未变则复用既有矩形与正在运行的动画，
    // 不重建、不重启——这样其他未完成任务每秒推进进度触发刷新时，不会打断/重置已完成任务的闪烁。
    private sealed class BlinkVisual
    {
        public Rectangle Rect = null!;
        public string Geo = "";  // 几何 + 颜色签名；变化只更新矩形位置/尺寸/填充，不动 Opacity 动画
        public string Anim = ""; // 呼吸节奏 + 错峰参数（half:delay）签名；仅此变化才重启动画
    }
    private readonly Dictionary<string, BlinkVisual> _blinkVisuals = new();
    // 上一帧创建的「非闪烁」矩形：每帧只移除这批后重建，持久化闪烁矩形不受影响。
    private readonly List<Rectangle> _staticRects = new();
    // 上次渲染的分段签名：未变化时跳过重建，避免每秒重置闪烁动画导致渐变不连续。
    private string _lastRenderSig = "";
    // 上次渲染日志签名：避免同一状态下重复输出 debug 日志。
    private string _lastRenderLogSig = "";
    // 上次图片位置日志签名：避免同一状态下重复输出 debug 日志。
    private string _lastSpriteLogSig = "";

    public OverlayWindow()
    {
        InitializeComponent();
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _hoverTimer.Tick += OnHoverTick;

        _gifTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(66), // ~15fps
        };
        _gifTimer.Tick += OnGifTick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        NativeMethods.ApplyOverlayStyles(helper.Handle);

        var src = HwndSource.FromHwnd(helper.Handle);
        src?.AddHook(WndProc);

        _hoverTimer.Start();
        _gifTimer.Start();
    }

    // 所有命中测试返回 HTTRANSPARENT，使鼠标点击与移动直接穿透到下层窗口。
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(NativeMethods.HTTRANSPARENT);
        }
        return IntPtr.Zero;
    }

    /// <summary>根据广播状态更新本窗口：尺寸、位置与分段填充。</summary>
    public void UpdateState(StateMessage msg, int barHeightPx)
    {
        // Headless 在无活跃任务时广播 "segments": null（Go nil 切片），需规整为空集合，避免空引用。
        var all = msg.Segments ?? new List<Segment>();
        _segments = all.Where(s => string.IsNullOrEmpty(s.Position) || s.Position == Position).ToList();
        _barHeightPx = Math.Clamp(barHeightPx, 1, 10);

        // 重算本帧应闪烁的任务（到期 + 行为含 blink/celebrate）；已查看集合裁剪到仍在闪烁的任务，
        // 以便任务被重新设定时间后再次到期可重新闪烁。
        _blinkingIds.Clear();
        foreach (var s in _segments)
            if (s.Expired && IsBlinkBehavior(s))
                _blinkingIds.Add(s.TaskId);
        _acknowledgedBlink.IntersectWith(_blinkingIds);

        bool show = msg.Visible && _segments.Count > 0;
        // 预读图片实际缩放后的尺寸，作为图片区域的厚度：
        // top/bottom 取高度，left/right 取宽度（图片水平放置于进度条旁）。
        _imageMaxThickness = 0;
        foreach (var s in _segments)
        {
            if (ImageSprite.IsUsable(s.Gif))
            {
                var maxSize = s.ImageMaxSize > 0 ? s.ImageMaxSize : 15;
                var (scaledW, scaledH) = GetScaledImageSize(s.Gif!, maxSize);
                // 旋转 90°/270° 时占用的宽高互换，带厚度需按旋转后的有效尺寸取。
                bool quarter = IsQuarterTurn(s.ImageRotation);
                var ew = quarter ? scaledH : scaledW;  // 水平方向占用
                var eh = quarter ? scaledW : scaledH;  // 垂直方向占用
                var thickness = IsVertical ? ew : eh;
                if (thickness > _imageMaxThickness) _imageMaxThickness = thickness;
            }
        }

        UpdateWindowBounds();
        Render();
        UpdateSprites(show);

        if (show)
        {
            if (!IsVisible) Show();
        }
        else
        {
            HoverPopup.IsOpen = false;
            if (IsVisible) Hide();
        }
    }

    private void UpdateWindowBounds()
    {
        var screen = SystemParameters.WorkArea;
        if (IsVertical)
        {
            Width = _barHeightPx + _imageMaxThickness;
            Height = screen.Height;
            Top = screen.Top;
            Left = Position == PositionLeft ? screen.Left : screen.Right - Width;
        }
        else
        {
            Width = screen.Width;
            Height = _barHeightPx + _imageMaxThickness;
            Left = screen.Left;
            Top = Position == PositionTop ? screen.Top : screen.Bottom - Height;
        }
    }

    // 在色段矩形上挂柔和 alpha 渐变动画（合成级，平滑且持续）。
    // halfPeriodSec：单程时长；beginDelaySec：错峰偏移（多任务用）。
    // 关键：以全局锚点 BlinkAnchorUtc 计算当前应处的相位，用负 BeginTime 让动画“仿佛已在过去开始”，
    // 这样无论各窗口何时调用本方法，都会对齐到同一墙钟相位 → 主条与三个氛围条同步。
    private static void StartBlink(Rectangle r, double halfPeriodSec, double beginDelaySec)
    {
        double period = 2.0 * halfPeriodSec; // AutoReverse：一来一回 = 2×单程
        double elapsed = (DateTime.UtcNow - BlinkAnchorUtc).TotalSeconds;
        double pos = ((elapsed + beginDelaySec) % period + period) % period; // 当前应处相位 [0, period)
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = BlinkMinAlpha,
            Duration = TimeSpan.FromSeconds(halfPeriodSec),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromSeconds(-pos), // 负起始：已推进到全局相位 pos
        };
        r.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>用户已查看到期提醒（如打开设置）：停止当前闪烁脉冲并复位透明度。</summary>
    public void AcknowledgeBlink()
    {
        foreach (var id in _blinkingIds) _acknowledgedBlink.Add(id);
        // 遍历权威的持久化矩形集合（_blinkRects 在 early-return 帧可能过时，无法可靠覆盖侧条窗口）。
        foreach (var bv in _blinkVisuals.Values)
        {
            bv.Rect.BeginAnimation(UIElement.OpacityProperty, null); // 解除动画
            bv.Rect.Opacity = 1.0;
            bv.Anim = ""; // 标记动画已停；下次若仍需闪烁会按需重启
        }
        _blinkRects.Clear();
    }

    // 依据当前分段维护图片精灵：按 fillEnd 定位到进度前沿，跟随进度移动。
    private void UpdateSprites(bool show)
    {
        if (!show)
        {
            ClearSprites();
            return;
        }

        var wanted = _segments.Where(s => ImageSprite.IsUsable(s.Gif)).ToList();

        // 移除已不需要、路径变更或最大尺寸变更的精灵
        foreach (var id in _sprites.Keys.ToList())
        {
            var seg = wanted.FirstOrDefault(s => s.TaskId == id);
            if (seg == null || seg.Gif != _sprites[id].Path || seg.ImageMaxSize != _sprites[id].MaxSize)
            {
                GifCanvas.Children.Remove(_sprites[id].Element);
                _sprites[id].Dispose();
                _sprites.Remove(id);
            }
        }

        double w = Width;
        double h = Height;
        // debug 日志：图片位置计算状态变化时输出一次。
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Position).Append('|').Append(Direction).Append('|');
            foreach (var seg in wanted)
            {
                bool rev = IsSegReverse(seg);
                double localFill = rev ? 100.0 - seg.FillEnd : seg.FillEnd;
                sb.Append(seg.TaskId).Append(':').Append(rev ? "rev" : "fwd").Append(':').Append(localFill).Append(';');
            }
            string sig = sb.ToString();
            if (sig != _lastSpriteLogSig)
            {
                _lastSpriteLogSig = sig;
                DesktopLog.Info($"UpdateSprites pos={Position} dir={Direction} {sig}");
            }
        }
        foreach (var seg in wanted)
        {
            if (!_sprites.TryGetValue(seg.TaskId, out var sprite))
            {
                try
                {
                    var maxSize = seg.ImageMaxSize > 0 ? seg.ImageMaxSize : 15;
                    sprite = new ImageSprite(seg.Gif!, maxSize);
                }
                catch
                {
                    continue; // 损坏或无法解码的图片直接跳过
                }
                _sprites[seg.TaskId] = sprite;
                GifCanvas.Children.Add(sprite.Element);
            }

            // 始终绕图片中心 (0.5,0.5) 旋转：包围盒只在中心两侧对称扩展，便于按有效尺寸贴边。
            sprite.SetRotation(seg.ImageRotation, 0.5, 0.5);

            // 正向：填充前沿 = FillEnd
            // 反向：绕整条轨道(100%)镜像 → 前沿 = 100 - FillEnd
            double localFill = seg.FillEnd;
            if (IsSegReverse(seg))
            {
                localFill = 100.0 - seg.FillEnd;
            }
            double front = localFill / 100.0 * (IsVertical ? h : w);

            // 旋转 90°/270° 时图片占用的宽高互换；用旋转后的有效尺寸 (ew,eh) 计算贴边位置。
            bool quarter = IsQuarterTurn(seg.ImageRotation);
            double ew = quarter ? sprite.Height : sprite.Width;   // 水平方向占用
            double eh = quarter ? sprite.Width  : sprite.Height;  // 垂直方向占用

            // 绕中心旋转后视觉包围盒中心 = 布局框中心，故按“有效包围盒中心”定位即可贴住进度条。
            double centerX, centerY;
            if (IsVertical)
            {
                // left：包围盒左缘贴进度条右缘；right：包围盒右缘贴进度条左缘。
                centerX = Position == PositionLeft ? _barHeightPx + ew / 2.0
                                                   : _imageMaxThickness - ew / 2.0;
                centerY = front;
                if (h >= eh) centerY = Math.Clamp(centerY, eh / 2.0, h - eh / 2.0);
            }
            else
            {
                // top：包围盒上缘贴进度条下缘；bottom：包围盒下缘贴进度条上缘。
                centerY = Position == PositionTop ? _barHeightPx + eh / 2.0
                                                 : _imageMaxThickness - eh / 2.0;
                centerX = front;
                if (w >= ew) centerX = Math.Clamp(centerX, ew / 2.0, w - ew / 2.0);
            }

            Canvas.SetLeft(sprite.Element, centerX - sprite.Width / 2.0);
            Canvas.SetTop(sprite.Element, centerY - sprite.Height / 2.0);
        }
    }

    // 旋转角是否为 90°/270°（此时图片占用宽高互换）。
    private static bool IsQuarterTurn(double angleDeg)
    {
        int q = ((int)Math.Round(angleDeg / 90.0)) % 4;
        if (q < 0) q += 4;
        return q == 1 || q == 3;
    }

    /// <summary>
    /// 预读图片实际尺寸，按 maxSize（高度上限）缩放后返回 (缩放后宽度, 缩放后高度)。
    /// 结果带缓存，避免每秒多次刷新时重复读取文件。
    /// </summary>
    private (double width, double height) GetScaledImageSize(string imagePath, double maxSize)
    {
        var cacheKey = (imagePath, maxSize);
        if (_imageSizeCache.TryGetValue(cacheKey, out var cached))
            return cached;

        double w = maxSize, h = maxSize; // 读取失败时返回配置值作为兜底
        try
        {
            if (File.Exists(imagePath))
            {
                var bytes = File.ReadAllBytes(imagePath);
                using var stream = new MemoryStream(bytes);
                using var bitmap = (Bitmap)Image.FromStream(stream);
                double scale = 1.0;
                if (bitmap.Height > maxSize && bitmap.Height > 0)
                    scale = maxSize / bitmap.Height;
                h = Math.Max(1, bitmap.Height * scale);
                w = Math.Max(1, bitmap.Width * scale);
            }
        }
        catch { /* 忽略损坏文件，使用兜底值 */ }

        _imageSizeCache[cacheKey] = (w, h);
        return (w, h);
    }

    private void ClearSprites()
    {
        foreach (var s in _sprites.Values)
        {
            GifCanvas.Children.Remove(s.Element);
            s.Dispose();
        }
        _sprites.Clear();
    }

    private void OnGifTick(object? sender, EventArgs e)
    {
        if (_sprites.Count == 0) return;
        foreach (var s in _sprites.Values) s.Advance();
    }

    private void Render()
    {
        double w = Width;
        double h = Height;
        if (w <= 0 || h <= 0) return;

        // 分段签名完全未变时整体跳过。注意：签名涵盖所有段的 FillEnd，未完成任务每秒推进会令其变化；
        // 此时仍会进入下方重建，但只重建「非闪烁段」，闪烁段按 taskId 持久化复用、动画不被打断。
        string sig = BuildRenderSignature(w, h);
        if (sig == _lastRenderSig && BarCanvas.Children.Count > 0) return;
        _lastRenderSig = sig;

        // debug 日志：渲染状态变化时输出一次，便于定位方向/位置问题。
        if (sig != _lastRenderLogSig)
        {
            _lastRenderLogSig = sig;
            foreach (var seg in _segments)
            {
                bool rev = IsSegReverse(seg);
                DesktopLog.Info($"Render pos={Position} dir={Direction} segDir={seg.Direction} reverse={rev} " +
                               $"barStart={seg.BarStart} barEnd={seg.BarEnd} fillEnd={seg.FillEnd}");
            }
        }

        // 预计算本帧需闪烁的任务及其错峰序号：跨四条边按 taskId 排序保持一致，
        // 使同一任务在各边同步、不同任务依次错峰。
        var blinkIds = _segments
            .Where(s => s.Expired && !_acknowledgedBlink.Contains(s.TaskId) && IsBlinkBehavior(s))
            .Select(s => s.TaskId).Distinct()
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        var blinkOrder = new Dictionary<string, int>(blinkIds.Count);
        for (int i = 0; i < blinkIds.Count; i++) blinkOrder[blinkIds[i]] = i;
        int blinkCount = blinkIds.Count;
        var blinkSet = new HashSet<string>(blinkIds);

        // 1) 回收不再闪烁的持久化矩形（任务恢复进行中 / 被确认 / 段消失）。
        foreach (var id in _blinkVisuals.Keys.ToList())
        {
            if (!blinkSet.Contains(id))
            {
                var bv = _blinkVisuals[id];
                bv.Rect.BeginAnimation(UIElement.OpacityProperty, null);
                BarCanvas.Children.Remove(bv.Rect);
                _blinkVisuals.Remove(id);
            }
        }

        // 2) 仅移除上一帧的「非闪烁」矩形；持久化闪烁矩形保留在画布与动画中。
        foreach (var r in _staticRects) BarCanvas.Children.Remove(r);
        _staticRects.Clear();
        _blinkRects.Clear();

        // 仅绘制已填充部分（barStart → fillEnd），未完成部分不画任何底色（保持透明、不可点击）。
        foreach (var seg in _segments)
        {
            if (!TryComputeFillRect(seg, w, h, out double rl, out double rt, out double rw, out double rh))
                continue;

            if (blinkSet.Contains(seg.TaskId))
            {
                // 多任务时加长渐变并按序错峰；单任务保持原有节奏、无延时。
                double half = blinkCount > 1 ? BlinkHalfPeriodMultiSec : BlinkHalfPeriodSec;
                // 错峰延时在整个呼吸周期(2×half)内按任务数均匀铺开：2 个任务即反相、明暗交替明显。
                double delay = blinkCount > 1 && blinkOrder.TryGetValue(seg.TaskId, out var idx)
                    ? idx * (2.0 * half / blinkCount) : 0;
                // 几何与动画分离：多任务打包布局下，进行中任务推进会令已完成段像素平移，
                // 但呼吸节奏不变——此时只搬动矩形、不重启动画，避免每秒重置导致呼吸丢失。
                string geo = $"{rl:F2}:{rt:F2}:{rw:F2}:{rh:F2}:{seg.Color}";
                string animSig = $"{half}:{delay}";

                if (_blinkVisuals.TryGetValue(seg.TaskId, out var bv))
                {
                    if (bv.Geo != geo)
                    {
                        bv.Rect.Width = rw;
                        bv.Rect.Height = rh;
                        bv.Rect.Fill = new SolidColorBrush(ParseColor(seg.Color));
                        Canvas.SetLeft(bv.Rect, rl);
                        Canvas.SetTop(bv.Rect, rt);
                        bv.Geo = geo;
                    }
                    if (bv.Anim != animSig)
                    {
                        // 仅呼吸节奏/错峰参数变化（单↔多任务切换、错峰序号变动）才重启动画。
                        bv.Anim = animSig;
                        StartBlink(bv.Rect, half, delay);
                    }
                }
                else
                {
                    var rect = new Rectangle
                    {
                        Width = rw,
                        Height = rh,
                        Fill = new SolidColorBrush(ParseColor(seg.Color)),
                    };
                    Canvas.SetLeft(rect, rl);
                    Canvas.SetTop(rect, rt);
                    BarCanvas.Children.Add(rect);
                    bv = new BlinkVisual { Rect = rect, Geo = geo, Anim = animSig };
                    _blinkVisuals[seg.TaskId] = bv;
                    StartBlink(rect, half, delay);
                }
                _blinkRects.Add(bv.Rect);
            }
            else
            {
                var rect = new Rectangle
                {
                    Width = rw,
                    Height = rh,
                    Fill = new SolidColorBrush(ParseColor(seg.Color)),
                };
                Canvas.SetLeft(rect, rl);
                Canvas.SetTop(rect, rt);
                BarCanvas.Children.Add(rect);
                _staticRects.Add(rect);
            }
        }
    }

    // 计算某段已填充矩形的画布坐标与尺寸（含正/反向镜像）。填充长度<=0 时返回 false（跳过绘制）。
    private bool TryComputeFillRect(Segment seg, double w, double h,
        out double left, out double top, out double width, out double height)
    {
        left = top = width = height = 0;

        // 正向：填充区 = [BarStart, FillEnd]
        // 反向：绕整条轨道(100%)镜像，端点各取 100 - x → 填充区 = [100 - FillEnd, 100 - BarStart]
        double localStart = seg.BarStart;
        double localFill  = seg.FillEnd;
        if (IsSegReverse(seg))
        {
            localStart = 100.0 - seg.FillEnd;
            localFill  = 100.0 - seg.BarStart;
        }

        if (IsVertical)
        {
            double y0 = localStart / 100.0 * h;
            double yFill = localFill / 100.0 * h;
            double fillHeight = yFill - y0;
            if (fillHeight <= 0) return false;
            width = _barHeightPx;
            height = fillHeight;
            left = Position == PositionLeft ? 0 : _imageMaxThickness;
            top = y0;
            return true;
        }
        else
        {
            double x0 = localStart / 100.0 * w;
            double xFill = localFill / 100.0 * w;
            double fillWidth = xFill - x0;
            if (fillWidth <= 0) return false;
            width = fillWidth;
            height = _barHeightPx;
            left = x0;
            top = Position == PositionTop ? 0 : _imageMaxThickness;
            return true;
        }
    }

    private static bool IsBlinkBehavior(Segment seg) =>
        seg.Behaviors != null && (seg.Behaviors.Contains("blink") || seg.Behaviors.Contains("celebrate"));

    // 渲染签名：涵盖所有影响绘制与闪烁判定的输入；据此决定是否需要重建。
    private string BuildRenderSignature(double w, double h)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Position).Append('|').Append(Direction).Append('|')
          .Append(w).Append('|').Append(h).Append('|').Append(_barHeightPx).Append('|');
        foreach (var s in _segments)
        {
            bool blink = s.Behaviors != null && s.Behaviors.Contains("blink");
            sb.Append(s.TaskId).Append(':').Append(s.BarStart).Append(':').Append(s.FillEnd)
              .Append(':').Append(s.Color).Append(':').Append(s.Expired ? 1 : 0)
              .Append(':').Append(blink && !_acknowledgedBlink.Contains(s.TaskId) ? 1 : 0)
              .Append(';');
        }
        return sb.ToString();
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _segments.Count == 0)
        {
            HoverPopup.IsOpen = false;
            return;
        }
        if (!NativeMethods.GetCursorPos(out var p)) return;

        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget == null) return;
        var dip = src.CompositionTarget.TransformFromDevice.Transform(new Point(p.X, p.Y));

        // 悬停仅作用于进度条本身的厚度带（不含内侧图片区）。
        bool inBarBand;
        double pct;
        if (IsVertical)
        {
            inBarBand = dip.Y >= Top && dip.Y <= Top + Height && dip.X >= Left && dip.X <= Left + _barHeightPx;
            pct = (dip.Y - Top) / Height * 100.0;
        }
        else
        {
            inBarBand = dip.Y >= Top && dip.Y <= Top + _barHeightPx && dip.X >= Left && dip.X <= Left + Width;
            pct = (dip.X - Left) / Width * 100.0;
        }
        if (!inBarBand)
        {
            HoverPopup.IsOpen = false;
            return;
        }

        if (IsReverse) pct = 100.0 - pct;

        // 仅已填充（彩色）部分响应悬停；未完成部分透明、不交互。
        var hit = _segments.FirstOrDefault(s => pct >= s.BarStart && pct <= s.FillEnd);
        if (hit == null)
        {
            HoverPopup.IsOpen = false;
            return;
        }

        HoverText.Text = $"{hit.Name}　{FormatCountdown(hit.EndAt)}";
        if (IsVertical)
        {
            HoverPopup.HorizontalOffset = Position == PositionLeft
                ? Left + Width + 8
                : Left - 8 - HoverText.ActualWidth;
            HoverPopup.VerticalOffset = dip.Y + 8;
        }
        else
        {
            HoverPopup.HorizontalOffset = dip.X + 8;
            HoverPopup.VerticalOffset = Position == PositionTop
                ? Top + Height + 2
                : Top - HoverText.ActualHeight - 2;
        }
        HoverPopup.IsOpen = true;
    }

    // 倒计时文案：距 endAt 的剩余时间。≥1 天显示「N 天 HH:mm:ss」，否则「HH:mm:ss」；已过显示「已到期」。
    private static string FormatCountdown(DateTimeOffset endAt)
    {
        var remaining = endAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "已到期";
        return remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays} 天 {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0xFF, 0x6B, 0x35); }
    }

    /// <summary>彻底关闭（退出时调用）。</summary>
    public void ForceClose()
    {
        _hoverTimer.Stop();
        _gifTimer.Stop();
        ClearSprites();
        HoverPopup.IsOpen = false;
        Close();
    }
}
