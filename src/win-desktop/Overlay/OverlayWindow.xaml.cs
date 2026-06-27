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
    private const double BlinkMinAlpha = 0.05;

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
    // 上次渲染的分段签名：未变化时跳过重建，避免每秒重置闪烁动画导致渐变不连续。
    private string _lastRenderSig = "";

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
                var thickness = IsVertical ? scaledW : scaledH;
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
    private static void StartBlink(Rectangle r)
    {
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = BlinkMinAlpha,
            Duration = TimeSpan.FromSeconds(BlinkHalfPeriodSec),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        r.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>用户已查看到期提醒（如打开设置）：停止当前闪烁脉冲并复位透明度。</summary>
    public void AcknowledgeBlink()
    {
        foreach (var id in _blinkingIds) _acknowledgedBlink.Add(id);
        foreach (var r in _blinkRects)
        {
            r.BeginAnimation(UIElement.OpacityProperty, null); // 解除动画
            r.Opacity = 1.0;
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
        // 旋转中心比例（相对于图片自身尺寸）
        // top/bottom 保持图片水平并让对应边缘吸附进度条；
        // left/right 将图片水平放置于进度条旁，中心对齐 fillEnd。
        double cx = 0.5, cy = 0.5;
        switch (Position)
        {
            case PositionTop:    cy = 0; break;
            case PositionBottom: cy = 1; break;
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

            // 设置旋转中心和角度
            sprite.SetRotation(seg.ImageRotation, cx, cy);

            double localFill = IsSegReverse(seg) ? 100.0 - seg.FillEnd : seg.FillEnd;
            double front = localFill / 100.0 * (IsVertical ? h : w);

            double left, top;
            if (IsVertical)
            {
                // left/right 图片水平放置于进度条旁，窗口宽度 = 进度条粗细 + 图片缩放后的宽度
                if (Position == PositionLeft)
                {
                    // 进度条在左，图片在右：图片左边缘紧贴进度条右边缘
                    left = _barHeightPx;
                }
                else // PositionRight
                {
                    // 进度条在右，图片在左：图片右边缘紧贴进度条左边缘
                    left = _imageMaxThickness - sprite.Width;
                }
                top = front - sprite.Height * cy;
                if (top < 0) top = 0;
                if (top > h - sprite.Height) top = h - sprite.Height;
            }
            else
            {
                left = front - sprite.Width * cx;
                if (left < 0) left = 0;
                if (left > w - sprite.Width) left = w - sprite.Width;
                top = Position == PositionTop ? _barHeightPx : 0;
            }

            Canvas.SetLeft(sprite.Element, left);
            Canvas.SetTop(sprite.Element, top);
        }
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

        // 分段几何/颜色/到期态未变化时跳过重建，让已挂载的闪烁动画连续运行（避免每秒重置）。
        string sig = BuildRenderSignature(w, h);
        if (sig == _lastRenderSig && BarCanvas.Children.Count > 0) return;
        _lastRenderSig = sig;

        BarCanvas.Children.Clear();
        _blinkRects.Clear();

        // 仅绘制已填充部分（fillStart → fillEnd），未完成部分不画任何底色（保持透明、不可点击）。
        // reverse 方向时，填充从 BarEnd 向 BarStart 延伸，因此起点 = BarEnd - 填充长度。
        foreach (var seg in _segments)
        {
            double fillStart, fillEnd;
            if (IsSegReverse(seg))
            {
                fillEnd = seg.BarEnd;
                fillStart = fillEnd - (seg.BarEnd - seg.BarStart) * seg.FillEnd / 100.0;
            }
            else
            {
                fillStart = seg.BarStart;
                fillEnd = seg.BarStart + (seg.BarEnd - seg.BarStart) * seg.FillEnd / 100.0;
            }

            if (IsVertical)
            {
                double y0 = fillStart / 100.0 * h;
                double yFill = fillEnd / 100.0 * h;
                double fillHeight = yFill - y0;
                if (fillHeight <= 0) continue;

                var fill = new Rectangle
                {
                    Width = _barHeightPx,
                    Height = fillHeight,
                    Fill = new SolidColorBrush(ParseColor(seg.Color)),
                };
                double left = Position == PositionLeft ? 0 : _imageMaxThickness;
                Canvas.SetLeft(fill, left);
                Canvas.SetTop(fill, y0);
                BarCanvas.Children.Add(fill);
                MaybeStartBlink(fill, seg);
            }
            else
            {
                double x0 = fillStart / 100.0 * w;
                double xFill = fillEnd / 100.0 * w;
                double fillWidth = xFill - x0;
                if (fillWidth <= 0) continue;

                var fill = new Rectangle
                {
                    Width = fillWidth,
                    Height = _barHeightPx,
                    Fill = new SolidColorBrush(ParseColor(seg.Color)),
                };
                double top = Position == PositionTop ? 0 : _imageMaxThickness;
                Canvas.SetLeft(fill, x0);
                Canvas.SetTop(fill, top);
                BarCanvas.Children.Add(fill);
                MaybeStartBlink(fill, seg);
            }
        }
    }

    private void MaybeStartBlink(Rectangle fill, Segment seg)
    {
        if (seg.Expired && !_acknowledgedBlink.Contains(seg.TaskId) && IsBlinkBehavior(seg))
        {
            _blinkRects.Add(fill);
            StartBlink(fill);
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
