using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using UserControl = System.Windows.Controls.UserControl;

namespace Hope.Desktop.Controls;

/// <summary>
/// 列表日期列：前天~后天范围内，在「08:01 07-02」与「08:01 今天」间交替展示（停留 4s、渐变 0.5s）。
/// 相对文案按当前日历日计算；跨日/唤醒时只更新文案，不重启动画，避免渐变被掐掉。
/// 可见行为空时不启动渐变。
/// </summary>
public partial class AlternatingDateText : UserControl
{
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
    /// <summary>当前是否处于「绝对↔相对」交替动画中。</summary>
    private bool _crossFadeActive;
    private bool _gateSubscribed;

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
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AlternatingDateText ctrl) ctrl.RefreshTexts(restartAnimation: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        if (!_gateSubscribed)
        {
            ListTextCrossFade.ContentEnabledChanged += OnContentEnabledChanged;
            _gateSubscribed = true;
        }
        EnsureDayWatch();
        RefreshTexts(restartAnimation: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_gateSubscribed)
        {
            ListTextCrossFade.ContentEnabledChanged -= OnContentEnabledChanged;
            _gateSubscribed = false;
        }
        _dayWatch?.Stop();
        _dayWatch = null;
        StopCrossFade();
    }

    private void OnContentEnabledChanged() =>
        Dispatcher.BeginInvoke(() => RefreshTexts(restartAnimation: true));

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Dispatcher.BeginInvoke(RefreshRelativeIfNeeded);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock) return;
        Dispatcher.BeginInvoke(RefreshRelativeIfNeeded);
    }

    private void EnsureDayWatch()
    {
        if (_dayWatch != null) return;
        // 仅用于跨日检测；不要短周期 force 重启动画。
        _dayWatch = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _dayWatch.Tick += (_, _) => RefreshRelativeIfNeeded();
        _dayWatch.Start();
    }

    /// <summary>日历日未变则什么都不做；变了则优先就地改文案，尽量保留正在播放的渐变。</summary>
    private void RefreshRelativeIfNeeded()
    {
        if (DateTime.Today == _relativeAnchorDate) return;
        RefreshTexts(restartAnimation: false);
    }

    private void RefreshTexts(bool restartAnimation)
    {
        if (!Value.HasValue)
        {
            StopCrossFade();
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

        // 列表为空：停渐变，只留绝对日期。
        if (!ListTextCrossFade.ContentEnabled || relative == null)
        {
            StopCrossFade();
            RelativeText.Text = relative ?? "";
            RelativeText.Opacity = 0;
            AbsoluteText.Opacity = 1;
            return;
        }

        RelativeText.Text = relative;

        if (_crossFadeActive && !restartAnimation)
        {
            // 跨日只换文案，不动 Opacity / Storyboard。
            return;
        }

        AbsoluteText.Opacity = 1;
        RelativeText.Opacity = 0;
        StartCrossFade();
    }

    private void StopCrossFade()
    {
        _crossFade?.Stop();
        _crossFade = null;
        _crossFadeActive = false;
    }

    private void StartCrossFade()
    {
        StopCrossFade();
        _crossFade = ListTextCrossFade.Begin(AbsoluteText, RelativeText);
        _crossFadeActive = true;
    }
}
