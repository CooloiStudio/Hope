using System.Windows;
using System.Windows.Media.Animation;

namespace Hope.Desktop.Controls;

/// <summary>
/// 任务列表「百分比↔时长」「绝对日期↔相对日期」共用的停留/渐变时序。
/// 用全局相位对齐，使表头与各行在同一时刻切换。
/// </summary>
public static class ListTextCrossFade
{
    public const double HoldSeconds = 4;
    public const double FadeSeconds = 0.5;

    public static readonly TimeSpan CycleDuration =
        TimeSpan.FromSeconds(HoldSeconds * 2 + FadeSeconds * 2);

    /// <summary>
    /// 可见任务行数 &gt; 0 时才允许内容渐变；表头渐变另由调用方按「是否有进行中副文案」决定。
    /// </summary>
    public static bool ContentEnabled { get; private set; }

    public static event Action? ContentEnabledChanged;

    public static void SetContentEnabled(bool enabled)
    {
        if (ContentEnabled == enabled) return;
        ContentEnabled = enabled;
        ContentEnabledChanged?.Invoke();
    }

    /// <summary>在 primary / secondary 两元素上启动循环交叉渐变，并 Seek 到全局相位。</summary>
    public static Storyboard Begin(UIElement primary, UIElement secondary)
    {
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

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, Duration = cycleEnd };
        Storyboard.SetTarget(primaryKeys, primary);
        Storyboard.SetTargetProperty(primaryKeys, new PropertyPath(UIElement.OpacityProperty));
        Storyboard.SetTarget(secondaryKeys, secondary);
        Storyboard.SetTargetProperty(secondaryKeys, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(primaryKeys);
        sb.Children.Add(secondaryKeys);
        sb.Begin();

        // 对齐到全局相位：后加载的单元格 / 表头与已在播的行同步切换。
        var cycleMs = (long)CycleDuration.TotalMilliseconds;
        if (cycleMs > 0)
        {
            var phase = TimeSpan.FromMilliseconds(Environment.TickCount64 % cycleMs);
            sb.Seek(phase, TimeSeekOrigin.BeginTime);
        }

        return sb;
    }
}
