using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using UserControl = System.Windows.Controls.UserControl;

namespace Hope.Desktop.Controls;

/// <summary>
/// 列表日期列：前天~后天范围内，在「08:01 07-02」与「08:01 今天」间交替展示（停留 3s、渐变 0.5s）。
/// 相对文案按「当前日历日」计算；跨午夜或休眠唤醒后会重新映射，避免长期挂起仍显示过期的「今天」。
/// </summary>
public partial class AlternatingDateText : UserControl
{
    private const double HoldSeconds = 3;
    private const double FadeSeconds = 0.5;
    private static readonly TimeSpan CycleDuration = TimeSpan.FromSeconds(HoldSeconds * 2 + FadeSeconds * 2);

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(DateTimeOffset?),
            typeof(AlternatingDateText),
            new PropertyMetadata(null, OnValueChanged));

    private Storyboard? _crossFade;
    private DispatcherTimer? _dayWatch;
    /// <summary>生成 RelativeText 时所依据的本地日历日；变则需重算相对标签。</summary>
    private DateTime _relativeAnchorDate = DateTime.MinValue;

    public DateTimeOffset? Value
    {
        get => (DateTimeOffset?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public AlternatingDateText()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AlternatingDateText ctrl) ctrl.RefreshTexts();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        EnsureDayWatch();
        RefreshTexts();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _dayWatch?.Stop();
        _dayWatch = null;
        _crossFade?.Stop();
        _crossFade = null;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) RefreshTextsIfStaleOrForce(force: true);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // 休眠跨越午夜后 DispatcherTimer 可能晚一拍；唤醒后立刻按新「今天」重算。
        Dispatcher.BeginInvoke(() => RefreshTextsIfStaleOrForce(force: true));
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock) return;
        Dispatcher.BeginInvoke(() => RefreshTextsIfStaleOrForce(force: true));
    }

    private void EnsureDayWatch()
    {
        if (_dayWatch != null) return;
        // 周期短于动画一轮，跨日后很快纠正；休眠唤醒后定时器补火也会触发检查。
        _dayWatch = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _dayWatch.Tick += (_, _) => RefreshTextsIfStaleOrForce(force: false);
        _dayWatch.Start();
    }

    private void RefreshTextsIfStaleOrForce(bool force)
    {
        if (!force && DateTime.Today == _relativeAnchorDate) return;
        RefreshTexts();
    }

    private void RefreshTexts()
    {
        _crossFade?.Stop();
        _crossFade = null;

        if (!Value.HasValue)
        {
            AbsoluteText.Text = "—";
            RelativeText.Text = "";
            RelativeText.Opacity = 0;
            AbsoluteText.Opacity = 1;
            _relativeAnchorDate = DateTime.Today;
            return;
        }

        var now = DateTimeOffset.Now;
        _relativeAnchorDate = now.LocalDateTime.Date;
        AbsoluteText.Text = TaskSchedule.FormatListAbsolute(Value.Value);
        var relative = TaskSchedule.FormatListRelative(Value.Value, now);
        if (relative == null)
        {
            RelativeText.Text = "";
            RelativeText.Opacity = 0;
            AbsoluteText.Opacity = 1;
            return;
        }

        RelativeText.Text = relative;
        AbsoluteText.Opacity = 1;
        RelativeText.Opacity = 0;
        StartCrossFade();
    }

    private void StartCrossFade()
    {
        var hold = TimeSpan.FromSeconds(HoldSeconds);
        var fadeEnd = hold + TimeSpan.FromSeconds(FadeSeconds);
        var hold2End = fadeEnd + hold;
        var cycleEnd = CycleDuration;

        var absKeys = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        absKeys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        absKeys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(hold)));
        absKeys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(fadeEnd)));
        absKeys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(hold2End)));
        absKeys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(cycleEnd)));

        var relKeys = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        relKeys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        relKeys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(hold)));
        relKeys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(fadeEnd)));
        relKeys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(hold2End)));
        relKeys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(cycleEnd)));

        _crossFade = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, Duration = cycleEnd };
        Storyboard.SetTarget(absKeys, AbsoluteText);
        Storyboard.SetTargetProperty(absKeys, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(relKeys, RelativeText);
        Storyboard.SetTargetProperty(relKeys, new PropertyPath(OpacityProperty));
        _crossFade.Children.Add(absKeys);
        _crossFade.Children.Add(relKeys);
        _crossFade.Begin();
    }
}
