using System.Windows;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;

namespace Hope.Desktop.Controls;

/// <summary>
/// 列表进度列：进行中时在「百分比」与「剩余/总时长」间交替展示（停留 4s、渐变 0.5s）；
/// 已完成 / 未开始 / 已到期等仅显示主文案。秒级刷新只换文案，不重启动画。
/// 列宽由列表固定，超长文案省略显示。可见行为空时不启动渐变。
/// </summary>
public partial class AlternatingProgressText : UserControl
{
    public static readonly DependencyProperty PrimaryProperty =
        DependencyProperty.Register(
            nameof(Primary),
            typeof(string),
            typeof(AlternatingProgressText),
            new PropertyMetadata("", OnTextChanged));

    public static readonly DependencyProperty SecondaryProperty =
        DependencyProperty.Register(
            nameof(Secondary),
            typeof(string),
            typeof(AlternatingProgressText),
            new PropertyMetadata(null, OnTextChanged));

    private Storyboard? _crossFade;
    private bool _crossFadeActive;
    private bool _hadSecondary;
    private bool _gateSubscribed;

    public string Primary
    {
        get => (string)GetValue(PrimaryProperty);
        set => SetValue(PrimaryProperty, value);
    }

    public string? Secondary
    {
        get => (string?)GetValue(SecondaryProperty);
        set => SetValue(SecondaryProperty, value);
    }

    public AlternatingProgressText()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_gateSubscribed)
        {
            ListTextCrossFade.ContentEnabledChanged += OnContentEnabledChanged;
            _gateSubscribed = true;
        }
        RefreshTexts(restartAnimation: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_gateSubscribed)
        {
            ListTextCrossFade.ContentEnabledChanged -= OnContentEnabledChanged;
            _gateSubscribed = false;
        }
        StopCrossFade();
    }

    private void OnContentEnabledChanged() =>
        Dispatcher.BeginInvoke(() => RefreshTexts(restartAnimation: true));

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not AlternatingProgressText ctrl) return;
        bool hasSecondary = !string.IsNullOrEmpty(ctrl.Secondary);
        bool secondaryPresenceChanged = hasSecondary != ctrl._hadSecondary;
        // 有无副文案切换时重启动画；仅百分比/倒计时数字变化时保留渐变相位。
        ctrl.RefreshTexts(restartAnimation: secondaryPresenceChanged || !ctrl._crossFadeActive);
    }

    private void RefreshTexts(bool restartAnimation)
    {
        var primary = Primary ?? "";
        var secondary = Secondary;
        bool hasSecondary = !string.IsNullOrEmpty(secondary);
        _hadSecondary = hasSecondary;

        PrimaryText.Text = string.IsNullOrEmpty(primary) ? "—" : primary;

        // 列表为空：停渐变，只留主文案（表头同理，由 Secondary 置空 + ContentEnabled 双重兜底）。
        if (!ListTextCrossFade.ContentEnabled || !hasSecondary)
        {
            StopCrossFade();
            SecondaryText.Text = hasSecondary ? secondary! : "";
            SecondaryText.Opacity = 0;
            PrimaryText.Opacity = 1;
            return;
        }

        // 副文案始终参与布局；列宽固定时由 TextTrimming 省略超长部分。
        SecondaryText.Text = secondary;

        if (_crossFadeActive && !restartAnimation)
            return;

        PrimaryText.Opacity = 1;
        SecondaryText.Opacity = 0;
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
        _crossFade = ListTextCrossFade.Begin(PrimaryText, SecondaryText);
        _crossFadeActive = true;
    }
}
