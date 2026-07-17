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
using Hope.Desktop.Services;
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

    // 颜色轮换：整周期目标时长（多色时按颜色数均分步长）；单色呼吸用原色↔暗色，整周期 3s（不用透明，避免透出下层非到期段）。
    private const double BlinkCycleTargetSec = 4.0;
    private const double BlinkStepMinSec = 1.0;
    private const double BlinkStepMaxSec = 1.5;
    private const double BlinkSingleBreatheStepSec = 1.5;
    // 全局相位锚点（App 启动时刻，跨所有 OverlayWindow 共享）：各边按同一墙钟相位轮换；
    // 当各边颜色序列相同时（四边环绕 / 全屏庆祝）即同步显示同一颜色。
    private static readonly DateTime BlinkAnchorUtc = DateTime.UtcNow;

    private readonly DispatcherTimer _hoverTimer;
    private EventHandler? _gifRenderingHandler;
    private long _lastGifTickMs;
    private readonly Dictionary<string, ImageSprite> _sprites = new();
    private List<Segment> _segments = new();
    private int _barHeightPx = 4;
    private double _imageMaxThickness;

    // 缓存图片缩放后的尺寸：(文件路径, maxSize) → (缩放后宽度, 缩放后高度)
    private readonly Dictionary<(string path, double maxSize), (double width, double height)> _imageSizeCache = new();
    /// <summary>正在后台探测尺寸的 key，避免重复启动 Task。</summary>
    private readonly HashSet<(string path, double maxSize)> _sizeProbeInFlight = new();
    /// <summary>正在后台读盘的任务 Id，避免重复启动 Task。</summary>
    private readonly HashSet<string> _spriteLoadInFlight = new();

    public string Position { get; set; } = PositionTop;
    public string Direction { get; set; } = "forward";

    /// <summary>当前屏幕布局；变更后需调用 RefreshLayout() 或等待 UpdateState 重算。</summary>
    public ScreenLayoutInfo? ScreenLayout { get; set; }

    private bool IsVertical => Position is PositionLeft or PositionRight;
    private bool IsReverse => Direction == "reverse";

    // 判断某条 Segment 的本地填充方向（优先用 Segment.Direction，回退到窗口级 Direction）
    private bool IsSegReverse(Segment seg)
    {
        if (!string.IsNullOrEmpty(seg.Direction))
            return seg.Direction == "reverse";
        return IsReverse;
    }

    // 闪烁状态：已查看（停止）任务集合、本帧应闪烁任务集合。
    private readonly HashSet<string> _acknowledgedBlink = new();
    private readonly HashSet<string> _blinkingIds = new();

    // 颜色轮换呼吸：本边所有闪烁段共用同一支可动画画刷；动画在「去重颜色序列」间循环平滑过渡
    // （单色时补一个透明 → 还原色↔透明呼吸）。动画挂在画刷上，与矩形几何解耦，
    // 故未完成任务每秒推进重建矩形不会打断呼吸；仅当颜色序列变化才重建动画。
    private readonly SolidColorBrush _blinkBrush = new(Colors.Transparent);
    private string _blinkSeqSig = ""; // 当前颜色序列签名；变化才重建动画，否则保持相位连续
    // 上次渲染的分段签名：未变化时跳过重建，避免每秒重置闪烁动画导致渐变不连续。
    private string _lastRenderSig = "";
    private IntPtr _hwnd;
    /// <summary>休眠唤醒后短暂挂起动图/悬停，避免合成栈未就绪时 LockBits 触发 CLR Fatal。</summary>
    private bool _renderingSuspended;
    private bool _pendingStateWhileSuspended;
    private bool _pendingVisible;

    public OverlayWindow()
    {
        InitializeComponent();
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _hoverTimer.Tick += OnHoverTick;
    }

    private void HookGifRendering()
    {
        if (_renderingSuspended) return;
        if (_gifRenderingHandler != null) return;
        _gifRenderingHandler = OnGifRendering;
        CompositionTarget.Rendering += _gifRenderingHandler;
        // 低频（仅显示/挂起切换时触发，非每帧）：标记 GIF 帧泵开始推进，便于与崩溃时刻交叉对照。
        DesktopLog.Info($"Overlay.GifRendering hooked pos={Position} sprites={_sprites.Count}");
    }

    private void UnhookGifRendering()
    {
        if (_gifRenderingHandler == null) return;
        CompositionTarget.Rendering -= _gifRenderingHandler;
        _gifRenderingHandler = null;
        DesktopLog.Info($"Overlay.GifRendering unhooked pos={Position}");
    }

    /// <summary>休眠唤醒：立刻停动图与悬停轮询，避免在 DWM 未就绪时触碰 GDI/WPF 像素。</summary>
    public void SuspendRendering()
    {
        _renderingSuspended = true;
        try
        {
            _hoverTimer.Stop();
            UnhookGifRendering();
            StopBlinkBrush();
            HoverPopup.IsOpen = false;
        }
        catch
        {
            // 唤醒瞬间合成异常时尽量吞掉，避免二次 Fatal
        }
    }

    /// <summary>唤醒稳定后恢复动图与悬停。</summary>
    public void ResumeRendering()
    {
        _renderingSuspended = false;
        try
        {
            if (_pendingStateWhileSuspended)
            {
                _pendingStateWhileSuspended = false;
                ApplyVisualState(_pendingVisible);
            }

            if (!IsVisible) return;
            _hoverTimer.Start();
            HookGifRendering();
        }
        catch
        {
            // ignore
        }
    }

    // 与 WPF 渲染管线同步推进动图；比 Background 优先级 DispatcherTimer 更不易被 UI 突发工作饿死。
    private void OnGifRendering(object? sender, EventArgs e)
    {
        if (_renderingSuspended || !IsVisible || _sprites.Count == 0) return;
        long now = Environment.TickCount64;
        if (now - _lastGifTickMs < 66) return; // ~15fps
        _lastGifTickMs = now;
        foreach (var s in _sprites.Values) s.Advance();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;
        NativeMethods.ApplyOverlayStyles(_hwnd);

        var src = HwndSource.FromHwnd(helper.Handle);
        src?.AddHook(WndProc);

        _hoverTimer.Start();
        HookGifRendering();
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
        _pendingVisible = msg.Visible;

        // 重算本帧应闪烁的任务（到期 + 行为含 blink/celebrate）；已查看集合裁剪到仍在闪烁的任务，
        // 以便任务被重新设定时间后再次到期可重新闪烁。
        _blinkingIds.Clear();
        foreach (var s in _segments)
            if (IsExpiredBlinkPaletteSegment(s))
                _blinkingIds.Add(s.TaskId);
        _acknowledgedBlink.IntersectWith(_blinkingIds);

        // 挂起时只缓存分段，禁止读图/改窗体/GDI，避免休眠唤醒瞬间 CLR Fatal。
        if (_renderingSuspended)
        {
            _pendingStateWhileSuspended = true;
            return;
        }

        ApplyVisualState(msg.Visible);
    }

    private void ApplyVisualState(bool visible)
    {
        bool show = visible && _segments.Count > 0;
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
            HookGifRendering();
        }
        else
        {
            HoverPopup.IsOpen = false;
            if (IsVisible) Hide();
        }
    }

    private void UpdateWindowBounds()
    {
        var screen = ScreenLayout?.EffectiveArea(Position) ?? SystemParameters.WorkArea;
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

        if (ScreenLayout?.ShouldRenderAboveTaskbar(Position) == true)
            NativeMethods.EnsureOverlayTopmost(_hwnd);
    }

    /// <summary>清除渲染缓存，强制下次按当前分段重绘（休眠唤醒等场景）。</summary>
    public void InvalidateVisualCache()
    {
        _lastRenderSig = "";
        _lastGifTickMs = 0;
    }

    /// <summary>在屏幕布局变化时立即重算窗口位置与尺寸，并触发一次重绘。</summary>
    public void RefreshLayout()
    {
        if (_renderingSuspended)
        {
            DesktopLog.Info($"Overlay.RefreshLayout skipped pos={Position} (rendering suspended)");
            return;
        }

        // 分步断点：以下 UpdateWindowBounds/Render/UpdateSprites 会改窗体几何并触碰位图/GDI。
        // 唤醒或熄屏后 DWM 未就绪时，任一步都可能打出不可捕获的 CLR Fatal（0x80131506）；
        // 最后一条存活日志即指向崩溃的具体步骤。
        DesktopLog.Info($"Overlay.RefreshLayout pos={Position} enter visible={IsVisible} sprites={_sprites.Count}");
        InvalidateVisualCache();
        UpdateWindowBounds();
        DesktopLog.Info($"Overlay.RefreshLayout pos={Position} bounds set W={Width:0} H={Height:0}");
        Render();
        DesktopLog.Info($"Overlay.RefreshLayout pos={Position} rendered");
        UpdateSprites(IsVisible);
        DesktopLog.Info($"Overlay.RefreshLayout pos={Position} sprites updated");
        if (IsVisible) HookGifRendering();
    }

    // 用「颜色序列」驱动本边共享画刷的轮换呼吸：在 seq 各颜色间循环平滑过渡（正弦缓动），
    // 步长随颜色数调整，使整周期约 BlinkCycleTargetSec。序列未变则保持动画连续；变化才重建并按全局锚点 Seek 对齐相位，
    // 使颜色序列相同的各边（四边环绕 / 全屏庆祝）同步显示同一颜色。
    private void UpdateBlinkBrush(List<Color> seq, int expiredColorCount)
    {
        string sig = string.Join(",", seq.Select(c => c.ToString()));
        if (sig == _blinkSeqSig) return; // 序列未变 → 不重启，相位连续
        _blinkSeqSig = sig;

        double stepSec = BlinkStepSeconds(expiredColorCount);
        double total = seq.Count * stepSec;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var anim = new ColorAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(total),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        for (int i = 0; i < seq.Count; i++)
            anim.KeyFrames.Add(new EasingColorKeyFrame(seq[i], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(i * stepSec)), ease));
        // 末尾回到首色，形成闭环。
        anim.KeyFrames.Add(new EasingColorKeyFrame(seq[0], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(total)), ease));
        anim.Freeze();

        var clock = anim.CreateClock();
        _blinkBrush.ApplyAnimationClock(SolidColorBrush.ColorProperty, clock);
        double elapsed = (DateTime.UtcNow - BlinkAnchorUtc).TotalSeconds;
        clock.Controller.Seek(TimeSpan.FromSeconds((elapsed % total + total) % total), TimeSeekOrigin.BeginTime);
    }

    private void StopBlinkBrush()
    {
        if (_blinkSeqSig.Length == 0) return;
        _blinkSeqSig = "";
        _blinkBrush.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
        _blinkBrush.Color = Colors.Transparent;
    }

    /// <summary>用户已查看到期提醒（如打开设置）：停止当前轮换呼吸。（当前无调用点，保留为可选 API。）</summary>
    public void AcknowledgeBlink()
    {
        foreach (var id in _blinkingIds) _acknowledgedBlink.Add(id);
        StopBlinkBrush();
    }

    // 依据当前分段维护图片精灵：按 fillEnd 定位到进度前沿，跟随进度移动。
    private void UpdateSprites(bool show)
    {
        if (!show)
        {
            ClearSprites();
            _spriteLoadInFlight.Clear();
            return;
        }

        var wanted = _segments.Where(s => ImageSprite.IsUsable(s.Gif)).ToList();

        // 移除已不需要、路径变更或最大尺寸变更的精灵（先移出视觉树再释放，缩短 UI 线程持锁时间）。
        foreach (var id in _sprites.Keys.ToList())
        {
            var seg = wanted.FirstOrDefault(s => s.TaskId == id);
            if (seg == null || seg.Gif != _sprites[id].Path || seg.ImageMaxSize != _sprites[id].MaxSize)
            {
                var sprite = _sprites[id];
                GifCanvas.Children.Remove(sprite.Element);
                _sprites.Remove(id);
                sprite.Dispose();
            }
        }

        double w = Width;
        double h = Height;
        foreach (var seg in wanted)
        {
            if (!_sprites.TryGetValue(seg.TaskId, out var sprite))
            {
                // 不在 UI 同步读盘：后台读字节，回 UI 再构造精灵。
                ScheduleSpriteLoad(seg);
                continue;
            }

            PositionSprite(seg, sprite, w, h);
        }
    }

    private void ScheduleSpriteLoad(Segment seg)
    {
        var path = seg.Gif!;
        var maxSize = seg.ImageMaxSize > 0 ? seg.ImageMaxSize : 15;
        var taskId = seg.TaskId;
        if (!_spriteLoadInFlight.Add(taskId)) return;

        _ = Task.Run(() =>
        {
            var bytes = ImageSprite.TryReadAllBytes(path);
            Dispatcher.BeginInvoke(() =>
            {
                _spriteLoadInFlight.Remove(taskId);
                if (bytes == null) return;
                // 分段可能已变更。
                var current = _segments.FirstOrDefault(s => s.TaskId == taskId);
                if (current == null || current.Gif != path ||
                    (current.ImageMaxSize > 0 ? current.ImageMaxSize : 15) != maxSize)
                    return;
                if (_sprites.ContainsKey(taskId)) return;
                if (!IsVisible) return;

                try
                {
                    var sprite = new ImageSprite(path, maxSize, bytes);
                    _sprites[taskId] = sprite;
                    GifCanvas.Children.Add(sprite.Element);
                    PositionSprite(current, sprite, Width, Height);
                }
                catch
                {
                    // 损坏或无法解码的图片直接跳过
                }
            });
        });
    }

    private void PositionSprite(Segment seg, ImageSprite sprite, double w, double h)
    {
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

    // 旋转角是否为 90°/270°（此时图片占用宽高互换）。
    private static bool IsQuarterTurn(double angleDeg)
    {
        int q = ((int)Math.Round(angleDeg / 90.0)) % 4;
        if (q < 0) q += 4;
        return q == 1 || q == 3;
    }

    /// <summary>
    /// 取图片缩放尺寸：缓存命中立即返回；未命中先返回兜底并后台探测，避免 UI 同步读盘。
    /// </summary>
    private (double width, double height) GetScaledImageSize(string imagePath, double maxSize)
    {
        var cacheKey = (imagePath, maxSize);
        if (_imageSizeCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // 未缓存：先用配置高度兜底，后台探测真实比例后刷新布局。
        var provisional = (maxSize, maxSize);
        _imageSizeCache[cacheKey] = provisional;
        ScheduleImageSizeProbe(cacheKey);
        return provisional;
    }

    private void ScheduleImageSizeProbe((string path, double maxSize) cacheKey)
    {
        if (!_sizeProbeInFlight.Add(cacheKey)) return;

        _ = Task.Run(() =>
        {
            var probed = ImageSprite.TryProbeScaledSize(cacheKey.path, cacheKey.maxSize);
            Dispatcher.BeginInvoke(() =>
            {
                _sizeProbeInFlight.Remove(cacheKey);
                if (probed == null) return;
                if (_imageSizeCache.TryGetValue(cacheKey, out var cur) && cur == probed.Value)
                    return;
                _imageSizeCache[cacheKey] = probed.Value;
                // 尺寸已知后重算条带厚度与精灵位置（不强制清渲染签名以外的状态）。
                RecomputeImageThicknessFromSegments();
                UpdateWindowBounds();
                InvalidateVisualCache();
                Render();
                UpdateSprites(IsVisible);
            });
        });
    }

    private void RecomputeImageThicknessFromSegments()
    {
        _imageMaxThickness = 0;
        foreach (var s in _segments)
        {
            if (!ImageSprite.IsUsable(s.Gif)) continue;
            var maxSize = s.ImageMaxSize > 0 ? s.ImageMaxSize : 15;
            var (scaledW, scaledH) = GetScaledImageSize(s.Gif!, maxSize);
            bool quarter = IsQuarterTurn(s.ImageRotation);
            var ew = quarter ? scaledH : scaledW;
            var eh = quarter ? scaledW : scaledH;
            var thickness = IsVertical ? ew : eh;
            if (thickness > _imageMaxThickness) _imageMaxThickness = thickness;
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

    private void Render()
    {
        double w = Width;
        double h = Height;
        if (w <= 0 || h <= 0) return;

        // 分段签名未变且画布非空时跳过重建。未完成任务每秒推进会改变签名 → 进入重建；
        // 但闪烁动画挂在共享画刷上（与矩形几何解耦），重建矩形不会打断呼吸。
        string sig = BuildRenderSignature(w, h);
        if (sig == _lastRenderSig && BarCanvas.Children.Count > 0) return;
        _lastRenderSig = sig;

        // 呼吸/庆祝渐变色：仅「已到期且未完成」且启用 blink/celebrate 的段（Expired=true）。
        // 单色用原色↔暗色脉冲（不用透明，避免全屏庆祝时透出进行中/未开始段的颜色）。
        var blinkSegs = _segments
            .Where(s => IsExpiredBlinkPaletteSegment(s) && !_acknowledgedBlink.Contains(s.TaskId))
            .ToList();
        var blinkSet = new HashSet<string>(blinkSegs.Select(s => s.TaskId));
        var blinkColors = blinkSegs
            .OrderBy(s => s.TaskId, StringComparer.Ordinal)
            .Select(s => ParseColor(s.Color))
            .Distinct()
            .ToList();

        if (blinkColors.Count == 0)
        {
            StopBlinkBrush();
        }
        else
        {
            var seq = BuildBlinkColorSequence(blinkColors);
            UpdateBlinkBrush(seq, blinkColors.Count);
        }

        BarCanvas.Children.Clear();

        // 仅绘制已填充部分（barStart → fillEnd），未完成部分不画任何底色（保持透明、不可点击）。
        // 闪烁段共用 _blinkBrush（颜色轮换呼吸）；非闪烁段用自身固定色。
        foreach (var seg in _segments)
        {
            if (!TryComputeFillRect(seg, w, h, out double rl, out double rt, out double rw, out double rh))
                continue;

            if (blinkSet.Contains(seg.TaskId))
            {
                var rect = new Rectangle { Width = rw, Height = rh, Fill = _blinkBrush };
                Canvas.SetLeft(rect, rl);
                Canvas.SetTop(rect, rt);
                BarCanvas.Children.Add(rect);
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

    /// <summary>是否纳入呼吸/庆祝渐变色板：已到期未完成（Segment.Expired）且含 blink/celebrate。</summary>
    private static bool IsExpiredBlinkPaletteSegment(Segment seg) =>
        seg.Expired && IsBlinkBehavior(seg);

    private static List<Color> BuildBlinkColorSequence(IReadOnlyList<Color> expiredColors)
    {
        if (expiredColors.Count == 0) return new List<Color>();
        if (expiredColors.Count == 1)
            return new List<Color> { expiredColors[0], DimColor(expiredColors[0]) };
        return expiredColors.ToList();
    }

    private static double BlinkStepSeconds(int expiredColorCount)
    {
        if (expiredColorCount <= 1)
            return BlinkSingleBreatheStepSec;
        return Math.Clamp(BlinkCycleTargetSec / expiredColorCount, BlinkStepMinSec, BlinkStepMaxSec);
    }

    private static Color DimColor(Color c, double factor = 0.38) =>
        Color.FromArgb(c.A, (byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    // 渲染签名：涵盖所有影响绘制与闪烁判定的输入；据此决定是否需要重建。
    private string BuildRenderSignature(double w, double h)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Position).Append('|').Append(Direction).Append('|')
          .Append(w).Append('|').Append(h).Append('|').Append(_barHeightPx).Append('|');
        foreach (var s in _segments)
        {
            bool blink = IsExpiredBlinkPaletteSegment(s);
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
        // 呼吸重叠区：文案按色板墙钟相位硬切换对应任务（不改进度条动画）。
        var hit = BlinkHoverResolver.Resolve(_segments, pct, DateTime.UtcNow, BlinkAnchorUtc, _acknowledgedBlink);
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
        UnhookGifRendering();
        ClearSprites();
        HoverPopup.IsOpen = false;
        Close();
    }
}
