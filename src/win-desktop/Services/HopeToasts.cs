using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;

namespace Hope.Desktop.Services;

/// <summary>Toast 语义等级（对齐 Fluent Success / Info / Caution / Danger）。</summary>
internal enum ToastLevel
{
    Success,
    Info,
    Caution,
    Danger,
}

/// <summary>
/// 配置窗操作反馈：自建 Toast（不使用 WPF-UI Snackbar）。
/// <list type="bullet">
/// <item>瞬时：底部倒计时 0→100、可 × 关闭、超时消失；同文案只刷新停留。</item>
/// <item>Sticky（业务校验）：无倒计时、无关闭钮；按 key 展示，逻辑恢复后 <see cref="ClearSticky"/> 关闭。</item>
/// </list>
/// </summary>
internal sealed class HopeToasts
{
    public const int MaxVisible = 3;
    public const double MaxWidth = 360;
    public const double MinWidth = 200;
    /// <summary>倒计时条高度（仅瞬时 Toast）。</summary>
    public const double CountdownBarHeight = 2;
    /// <summary>正文区内边距（上下合计）。</summary>
    public const double BodyPaddingV = 6; // 3+3
    /// <summary>文案行高（单行）。</summary>
    public const double LineHeightPx = 14;
    /// <summary>瞬时 Toast 内容总高度（正文+倒计时）。</summary>
    public const double ContentHeightPx = BodyPaddingV + LineHeightPx + CountdownBarHeight;
    /// <summary>Sticky Toast 内容高度（无倒计时条）。</summary>
    public const double StickyContentHeightPx = BodyPaddingV + LineHeightPx;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    private readonly Window _owner;
    private readonly Panel _host;
    private readonly List<Entry> _active = new();

    public HopeToasts(Window owner, Panel host)
    {
        _owner = owner;
        _host = host;
    }

    public bool CanShow =>
        _owner.IsLoaded && _owner.IsVisible && _owner.WindowState != WindowState.Minimized;

    public void Success(string message) => Show(message, ToastLevel.Success);

    public void Info(string message) => Show(message, ToastLevel.Info);

    public void Caution(string message) => Show(message, ToastLevel.Caution);

    public void Danger(string message) => Show(message, ToastLevel.Danger);

    /// <summary>
    /// 瞬时 Toast：有倒计时与关闭钮；超时或手动关闭。
    /// </summary>
    public void Show(string message, ToastLevel level, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        if (!_owner.Dispatcher.CheckAccess())
        {
            _owner.Dispatcher.BeginInvoke(() => Show(message, level, timeout));
            return;
        }

        if (!CanShow) return;

        var stay = timeout ?? DefaultTimeout;

        var existing = _active.Find(e =>
            e.StickyKey == null &&
            string.Equals(e.Message, message, StringComparison.Ordinal));
        if (existing != null)
        {
            ApplyLevel(existing, level);
            RestartStay(existing, stay);
            return;
        }

        EvictOldestTransientIfNeeded();

        var entry = BuildEntry(message, level, stay, stickyKey: null);
        _host.Children.Add(entry.Root);
        _active.Add(entry);
        RestartStay(entry, stay);
    }

    /// <summary>
    /// 业务校验 Sticky Toast：无倒计时、无关闭钮；同 key 只保留一条并更新文案；
    /// 业务条件恢复后调用 <see cref="ClearSticky"/> 关闭。
    /// </summary>
    public void ShowSticky(string key, string message, ToastLevel level = ToastLevel.Caution)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(message)) return;

        if (!_owner.Dispatcher.CheckAccess())
        {
            _owner.Dispatcher.BeginInvoke(() => ShowSticky(key, message, level));
            return;
        }

        if (!CanShow) return;

        var existing = _active.Find(e =>
            e.StickyKey != null &&
            string.Equals(e.StickyKey, key, StringComparison.Ordinal));
        if (existing != null)
        {
            existing.Message = message;
            existing.Text.Text = message;
            ApplyLevel(existing, level);
            return;
        }

        EvictOldestTransientIfNeeded();

        var entry = BuildEntry(message, level, stay: TimeSpan.Zero, stickyKey: key);
        _host.Children.Add(entry.Root);
        _active.Add(entry);
    }

    /// <summary>关闭指定 key 的 Sticky Toast（业务已符合时调用）。</summary>
    public void ClearSticky(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        if (!_owner.Dispatcher.CheckAccess())
        {
            _owner.Dispatcher.BeginInvoke(() => ClearSticky(key));
            return;
        }

        var existing = _active.Find(e =>
            e.StickyKey != null &&
            string.Equals(e.StickyKey, key, StringComparison.Ordinal));
        if (existing != null)
            Dismiss(existing);
    }

    private void EvictOldestTransientIfNeeded()
    {
        while (TransientCount() >= MaxVisible)
        {
            var idx = _active.FindIndex(e => e.StickyKey == null);
            if (idx < 0) break;
            RemoveAt(idx);
        }
    }

    private int TransientCount() => _active.Count(e => e.StickyKey == null);

    private Entry BuildEntry(string message, ToastLevel level, TimeSpan stay, string? stickyKey)
    {
        var sticky = stickyKey != null;
        var accent = LevelBrush(level);

        var accentBar = new Border
        {
            Width = 3,
            Background = accent,
            CornerRadius = new CornerRadius(4, 0, 0, 4),
        };

        var text = new TextBlock
        {
            Text = message,
            FontSize = 11,
            LineHeight = LineHeightPx,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("TextFillColorPrimaryBrush", Brushes.White) ?? Brushes.White,
            Margin = new Thickness(0, 0, sticky ? 0 : 4, 0),
        };

        var body = new DockPanel
        {
            LastChildFill = true,
            Height = LineHeightPx,
            Margin = new Thickness(8, 3, sticky ? 8 : 4, 3),
        };

        Button? close = null;
        if (!sticky)
        {
            close = new Button
            {
                Content = "×",
                Width = 16,
                Height = 16,
                Padding = new Thickness(0),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ThemeBrush("TextFillColorSecondaryBrush", Brushes.Gray) ?? Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "关闭",
            };
            DockPanel.SetDock(close, Dock.Right);
            body.Children.Add(close);
        }

        body.Children.Add(text);

        var contentGrid = new Grid();
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        ScaleTransform? countdownScale = null;
        Border? countdownFill = null;
        if (!sticky)
        {
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CountdownBarHeight) });
            countdownScale = new ScaleTransform(0, 1);
            countdownFill = new Border
            {
                Height = CountdownBarHeight,
                Background = accent,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = countdownScale,
                IsHitTestVisible = false,
            };
            var countdownTrack = new Border
            {
                Height = CountdownBarHeight,
                Background = BrushFrom("#22000000"),
                Child = countdownFill,
                ClipToBounds = true,
            };
            Grid.SetRow(body, 0);
            Grid.SetRow(countdownTrack, 1);
            contentGrid.Children.Add(body);
            contentGrid.Children.Add(countdownTrack);
        }
        else
        {
            contentGrid.Children.Add(body);
        }

        var chrome = new Grid();
        chrome.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        chrome.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(accentBar, 0);
        Grid.SetColumn(contentGrid, 1);
        chrome.Children.Add(accentBar);
        chrome.Children.Add(contentGrid);

        var root = new Border
        {
            Child = chrome,
            MinWidth = MinWidth,
            MaxWidth = MaxWidth,
            Height = sticky ? StickyContentHeightPx : ContentHeightPx,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            Background = OpaqueSurfaceBrush(),
            SnapsToDevicePixels = true,
            ClipToBounds = true,
        };

        DispatcherTimer? timer = sticky ? null : new DispatcherTimer { Interval = stay };
        var entry = new Entry(
            message,
            stickyKey,
            root,
            text,
            accentBar,
            countdownFill,
            countdownScale,
            timer);

        if (close != null)
            close.Click += (_, _) => Dismiss(entry);

        if (timer != null)
        {
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Dismiss(entry);
            };
        }

        ApplyLevel(entry, level);
        return entry;
    }

    private static void ApplyLevel(Entry entry, ToastLevel level)
    {
        var accent = LevelBrush(level);
        entry.AccentBar.Background = accent;
        if (entry.CountdownFill != null)
            entry.CountdownFill.Background = accent;
    }

    private static Brush LevelBrush(ToastLevel level) => level switch
    {
        ToastLevel.Success => ThemeBrushRequired("SystemFillColorSuccessBrush", BrushFrom("#0F7B0F")),
        ToastLevel.Caution => ThemeBrushRequired("SystemFillColorCautionBrush", BrushFrom("#9D5D00")),
        ToastLevel.Danger => ThemeBrushRequired("SystemFillColorCriticalBrush", BrushFrom("#C42B1C")),
        _ => ThemeBrushRequired("SystemFillColorAttentionBrush",
            ThemeBrushRequired("SystemAccentColorPrimaryBrush", BrushFrom("#0078D4"))),
    };

    private static Brush OpaqueSurfaceBrush()
    {
        var raw = ThemeBrush("SolidBackgroundFillColorBaseBrush", null)
                  ?? ThemeBrush("ApplicationBackgroundBrush", null)
                  ?? ThemeBrush("CardBackgroundFillColorDefaultBrush", null)
                  ?? BrushFrom("#FF202020");
        return OpaqueBrush(raw);
    }

    private static Brush OpaqueBrush(Brush brush)
    {
        if (brush is SolidColorBrush solid)
        {
            var c = solid.Color;
            if (c.A == 255) return solid;
            var opaque = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            if (opaque.CanFreeze) opaque.Freeze();
            return opaque;
        }

        return BrushFrom("#FF202020");
    }

    private static Brush? ThemeBrush(string key, Brush? fallback)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
                return b;
        }
        catch
        {
            // 资源缺失时回退
        }
        return fallback;
    }

    private static Brush ThemeBrushRequired(string key, Brush fallback) =>
        ThemeBrush(key, fallback) ?? fallback;

    private static SolidColorBrush BrushFrom(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    private static void RestartStay(Entry entry, TimeSpan stay)
    {
        if (entry.Timer == null || entry.CountdownScale == null) return;
        entry.Timer.Stop();
        entry.Timer.Interval = stay;
        entry.Timer.Start();
        StartCountdownVisual(entry, stay);
    }

    private static void StartCountdownVisual(Entry entry, TimeSpan stay)
    {
        if (entry.CountdownScale == null) return;
        StopCountdownVisual(entry);
        // 0→100：从左向右填满（ScaleX 0→1，原点在左侧）。
        entry.CountdownScale.ScaleX = 0;
        var anim = new DoubleAnimation(0, 1, stay) { FillBehavior = FillBehavior.Stop };
        entry.CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
    }

    private static void StopCountdownVisual(Entry entry)
    {
        if (entry.CountdownScale == null) return;
        entry.CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        entry.CountdownScale.ScaleX = 0;
    }

    private void Dismiss(Entry entry)
    {
        if (entry.Closing) return;
        entry.Closing = true;
        entry.Timer?.Stop();
        StopCountdownVisual(entry);
        _active.Remove(entry);
        if (_host.Children.Contains(entry.Root))
            _host.Children.Remove(entry.Root);
    }

    private void RemoveAt(int index)
    {
        if (index < 0 || index >= _active.Count) return;
        Dismiss(_active[index]);
    }

    private sealed class Entry(
        string message,
        string? stickyKey,
        Border root,
        TextBlock text,
        Border accentBar,
        Border? countdownFill,
        ScaleTransform? countdownScale,
        DispatcherTimer? timer)
    {
        public string Message { get; set; } = message;
        public string? StickyKey { get; } = stickyKey;
        public Border Root { get; } = root;
        public TextBlock Text { get; } = text;
        public Border AccentBar { get; } = accentBar;
        public Border? CountdownFill { get; } = countdownFill;
        public ScaleTransform? CountdownScale { get; } = countdownScale;
        public DispatcherTimer? Timer { get; } = timer;
        public bool Closing { get; set; }
    }
}
