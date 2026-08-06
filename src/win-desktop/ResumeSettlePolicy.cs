namespace Hope.Desktop;

/// <summary>
/// 休眠唤醒 settle 策略：锁屏常被误判为「全屏」，无限推迟会导致 Overlay 永久挂起。
/// 另：唤醒后分层窗口常出现不透明底，软刷新无法恢复，需在抑制窗结束后硬重建。
/// </summary>
public static class ResumeSettlePolicy
{
    /// <summary>因 fullscreen 最多再等几轮（每轮约 3s）；超出后强制恢复。</summary>
    public const int MaxFullscreenDefers = 5;

    /// <summary>
    /// 唤醒后禁止硬重建 Overlay 的时长（秒）。
    /// 须略长于默认 settle（10s），避免静默期刚结束就在 DWM 抖动中销毁 HWND。
    /// </summary>
    public const int OverlayHardResetSuppressSeconds = 12;

    /// <summary>是否应强制 settle，即使当前仍报 fullscreen。</summary>
    public static bool ShouldForceSettleDespiteFullscreen(int fullscreenDeferCount) =>
        fullscreenDeferCount >= MaxFullscreenDefers;

    /// <summary>距唤醒不足抑制窗时，硬重建应继续延后。</summary>
    public static bool ShouldDeferOverlayHardReset(TimeSpan sinceResume) =>
        sinceResume >= TimeSpan.Zero &&
        sinceResume < TimeSpan.FromSeconds(OverlayHardResetSuppressSeconds);
}
