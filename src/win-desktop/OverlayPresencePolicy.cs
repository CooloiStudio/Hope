namespace Hope.Desktop;

/// <summary>Overlay 窗口在 Win32 侧的实况采样，用于校验「WPF 以为在显示」与「系统里真的在显示」是否一致。</summary>
/// <param name="HandleAlive">窗口句柄仍有效。</param>
/// <param name="SystemVisible">IsWindowVisible 为真。</param>
/// <param name="Topmost">扩展样式仍含 WS_EX_TOPMOST。</param>
/// <param name="Layered">扩展样式仍含 WS_EX_LAYERED。</param>
/// <param name="HasArea">窗口矩形非空。</param>
public readonly record struct OverlayWindowFacts(
    bool HandleAlive,
    bool SystemVisible,
    bool Topmost,
    bool Layered,
    bool HasArea)
{
    public override string ToString() =>
        $"alive={HandleAlive} sysVisible={SystemVisible} topmost={Topmost} layered={Layered} area={HasArea}";
}

/// <summary>
/// Overlay 呈现健康度判定。
/// 休眠唤醒、锁屏解锁、独占全屏应用退出都可能让系统悄悄摘掉 WS_EX_TOPMOST，
/// 此时 WPF 侧 IsVisible/Topmost 仍为 true、Render 也照跑，但那条窄带已掉出置顶层被其他窗口盖住，
/// 表现为「进度条丢失、程序没卡死、手动刷新（销毁重建窗口）就回来」。
/// </summary>
public static class OverlayPresencePolicy
{
    /// <summary>重申置顶后仍不达标，说明该窗口已无法靠软刷新救回，只能销毁重建。</summary>
    public static bool NeedsRebuild(bool wpfExpectsVisible, OverlayWindowFacts facts)
    {
        // WPF 主动隐藏（无分段可画）时系统不可见是正常的，不能据此重建。
        if (!wpfExpectsVisible) return false;

        return !facts.HandleAlive ||
               !facts.SystemVisible ||
               !facts.Topmost ||
               !facts.Layered ||
               !facts.HasArea;
    }

    /// <summary>巡检用：仅置顶层级掉了，补一次 SetWindowPos 即可，无需重建。</summary>
    public static bool NeedsTopmostRepair(bool wpfExpectsVisible, OverlayWindowFacts facts) =>
        wpfExpectsVisible && facts.HandleAlive && facts.SystemVisible && !facts.Topmost;

    /// <summary>连续补置顶都没生效的次数上限；超过说明有别的程序在抢，退避一段时间不再拉锯。</summary>
    public const int MaxConsecutiveTopmostRepairs = 3;

    /// <summary>退避时长：独占全屏游戏之类会持续压制置顶，1Hz 硬顶会互相打架且刷屏日志。</summary>
    public static readonly TimeSpan TopmostRepairBackoff = TimeSpan.FromSeconds(60);

    /// <summary>补置顶后仍未生效，且已连续失败到上限，则进入退避。</summary>
    public static bool ShouldBackOffTopmostRepair(int consecutiveFailures) =>
        consecutiveFailures >= MaxConsecutiveTopmostRepairs;
}
