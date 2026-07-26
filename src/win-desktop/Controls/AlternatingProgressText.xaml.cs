using System.Windows;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;

namespace Hope.Desktop.Controls;

/// <summary>
/// 列表进度列：进行中时在「百分比」与「剩余/总时长」间交替展示（停留 4s、渐变 0.5s）；
/// 已完成 / 未开始 / 已到期等仅显示主文案。秒级刷新只换文案，不重启动画。
/// </summary>
public partial class AlternatingProgressText : UserControl
{
    private const double HoldSeconds = 4;
    private const double FadeSeconds = 0.5;
    private static readonly TimeSpan CycleDuration = TimeSpan.FromSeconds(HoldSeconds * 2 + FadeSeconds * 2);

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
        Loaded += (_, _) => RefreshTexts(restartAnimation: true);
        Unloaded += (_, _) => StopCrossFade();
    }

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

        if (!hasSecondary)
        {
            StopCrossFade();
            SecondaryText.Text = "";
            SecondaryText.Opacity = 0;
            PrimaryText.Opacity = 1;
            return;
        }

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

        var hold = TimeSpan.FromSeconds(HoldSeconds);
        var fadeEnd = hold + TimeSpan.FromSeconds(FadeSeconds);
        var hold2End = fadeEnd + hold;
        var cycleEnd = CycleDuration;

        var primaryKeys = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        primaryKeys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        primaryKeys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(hold)));
        primaryKeys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(fadeEnd)));
        primaryKeys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(hold2End)));
        primaryKeys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(cycleEnd)));

        var secondaryKeys = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        secondaryKeys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        secondaryKeys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(hold)));
        secondaryKeys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(fadeEnd)));
        secondaryKeys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(hold2End)));
        secondaryKeys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(cycleEnd)));

        _crossFade = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, Duration = cycleEnd };
        Storyboard.SetTarget(primaryKeys, PrimaryText);
        Storyboard.SetTargetProperty(primaryKeys, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(secondaryKeys, SecondaryText);
        Storyboard.SetTargetProperty(secondaryKeys, new PropertyPath(OpacityProperty));
        _crossFade.Children.Add(primaryKeys);
        _crossFade.Children.Add(secondaryKeys);
        _crossFade.Begin();
        _crossFadeActive = true;
    }
}
