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
/// 屏幕顶端分段彩色进度条。点击穿透、不可聚焦、不参与 Alt+Tab；
/// 唯一交互为悬停展示任务名（通过全局光标轮询实现，文档 §5.4）。
/// </summary>
public partial class OverlayWindow : Window
{
    // 图片精灵最大高度（进度条下方），单位 DIP。超过则等比缩小到此高度。
    private const double ImageMaxHeight = 15;

    // 闪烁脉冲参数：柔和正弦渐变（淡出至近透明再淡入），单程时长 → 全周期约 2×。
    private const double BlinkHalfPeriodSec = 0.8;
    private const double BlinkMinAlpha = 0.05;

    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _gifTimer;
    private readonly Dictionary<string, ImageSprite> _sprites = new();
    private List<Segment> _segments = new();
    private int _barHeightPx = 4;

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

    /// <summary>根据广播状态更新顶栏：尺寸、位置与分段填充。</summary>
    public void UpdateState(StateMessage msg, int barHeightPx)
    {
        // Headless 在无活跃任务时广播 "segments": null（Go nil 切片），需规整为空集合，避免空引用。
        _segments = msg.Segments ?? new List<Segment>();
        _barHeightPx = Math.Clamp(barHeightPx, 1, 10);

        // 重算本帧应闪烁的任务（到期 + 行为含 blink）；已查看集合裁剪到仍在闪烁的任务，
        // 以便任务被重新设定时间后再次到期可重新闪烁。
        _blinkingIds.Clear();
        foreach (var s in _segments)
            if (s.Expired && s.Behaviors != null && s.Behaviors.Contains("blink"))
                _blinkingIds.Add(s.TaskId);
        _acknowledgedBlink.IntersectWith(_blinkingIds);

        bool show = msg.Visible && _segments.Count > 0;
        bool hasImage = show && _segments.Any(s => ImageSprite.IsUsable(s.Gif));

        // 进度条本身恒为 barHeightPx；带图片时窗口向下扩展出图片区，但进度条粗细不变。
        Width = SystemParameters.PrimaryScreenWidth;
        Height = _barHeightPx + (hasImage ? ImageMaxHeight : 0);
        Left = 0;
        Top = 0;

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

        // 移除已不需要或路径变更的精灵
        foreach (var id in _sprites.Keys.ToList())
        {
            var seg = wanted.FirstOrDefault(s => s.TaskId == id);
            if (seg == null || seg.Gif != _sprites[id].Path)
            {
                GifCanvas.Children.Remove(_sprites[id].Element);
                _sprites[id].Dispose();
                _sprites.Remove(id);
            }
        }

        double w = Width;
        foreach (var seg in wanted)
        {
            if (!_sprites.TryGetValue(seg.TaskId, out var sprite))
            {
                try
                {
                    sprite = new ImageSprite(seg.Gif!, ImageMaxHeight);
                }
                catch
                {
                    continue; // 损坏或无法解码的图片直接跳过
                }
                _sprites[seg.TaskId] = sprite;
                GifCanvas.Children.Add(sprite.Element);
            }

            // 水平中心对齐进度前沿；垂直挂在进度条下方。
            double frontX = seg.FillEnd / 100.0 * w;
            double left = Math.Clamp(frontX - sprite.Width / 2, 0, Math.Max(0, w - sprite.Width));
            Canvas.SetLeft(sprite.Element, left);
            Canvas.SetTop(sprite.Element, _barHeightPx);
        }
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
        if (w <= 0) return;

        // 分段几何/颜色/到期态未变化时跳过重建，让已挂载的闪烁动画连续运行（避免每秒重置）。
        string sig = BuildRenderSignature(w);
        if (sig == _lastRenderSig && BarCanvas.Children.Count > 0) return;
        _lastRenderSig = sig;

        BarCanvas.Children.Clear();
        _blinkRects.Clear();

        // 仅绘制已填充部分（barStart → fillEnd），未完成部分不画任何底色（保持透明、不可点击）。
        // 进度条高度恒为 _barHeightPx，不随图片区扩展而变粗。
        foreach (var seg in _segments)
        {
            double x0 = seg.BarStart / 100.0 * w;
            double xFill = seg.FillEnd / 100.0 * w;
            double fillWidth = xFill - x0;
            if (fillWidth <= 0) continue;

            var fill = new Rectangle
            {
                Width = fillWidth,
                Height = _barHeightPx,
                Fill = new SolidColorBrush(ParseColor(seg.Color)),
            };
            Canvas.SetLeft(fill, x0);
            Canvas.SetTop(fill, 0);
            BarCanvas.Children.Add(fill);

            // 到期且行为含 blink、且用户尚未查看 → 挂柔和渐变动画。
            if (seg.Expired && !_acknowledgedBlink.Contains(seg.TaskId) &&
                seg.Behaviors != null && seg.Behaviors.Contains("blink"))
            {
                _blinkRects.Add(fill);
                StartBlink(fill);
            }
        }
    }

    // 渲染签名：涵盖所有影响绘制与闪烁判定的输入；据此决定是否需要重建。
    private string BuildRenderSignature(double w)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(w).Append('|').Append(_barHeightPx).Append('|');
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

        // 悬停仅作用于进度条本身的高度带（不含下方图片区）。
        bool inBarBand = dip.Y >= Top && dip.Y <= Top + _barHeightPx && dip.X >= Left && dip.X <= Left + Width;
        if (!inBarBand)
        {
            HoverPopup.IsOpen = false;
            return;
        }

        // 仅已填充（彩色）部分响应悬停；未完成部分透明、不交互。
        double pct = (dip.X - Left) / Width * 100.0;
        var hit = _segments.FirstOrDefault(s => pct >= s.BarStart && pct <= s.FillEnd);
        if (hit == null)
        {
            HoverPopup.IsOpen = false;
            return;
        }

        HoverText.Text = $"{hit.Name}　{FormatCountdown(hit.EndAt)}";
        HoverPopup.HorizontalOffset = dip.X + 8;
        HoverPopup.VerticalOffset = Top + _barHeightPx + 2;
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
