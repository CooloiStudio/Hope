using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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

    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _gifTimer;
    private readonly Dictionary<string, ImageSprite> _sprites = new();
    private List<Segment> _segments = new();
    private int _barHeightPx = 4;

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
        BarCanvas.Children.Clear();
        double w = Width;
        if (w <= 0) return;

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
        }
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

        HoverText.Text = $"{hit.Name}　{hit.Percent:0.#}%";
        HoverPopup.HorizontalOffset = dip.X + 8;
        HoverPopup.VerticalOffset = Top + _barHeightPx + 2;
        HoverPopup.IsOpen = true;
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0xFF, 0x6B, 0x35); }
    }

    /// <summary>到期闪烁提示（expiredBehavior=blink）。</summary>
    public void Blink()
    {
        int count = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            BarCanvas.Opacity = BarCanvas.Opacity < 0.5 ? 1.0 : 0.2;
            if (++count >= 8) { BarCanvas.Opacity = 1.0; timer.Stop(); }
        };
        timer.Start();
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
