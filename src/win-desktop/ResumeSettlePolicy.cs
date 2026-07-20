namespace Hope.Desktop;

/// <summary>
/// 休眠唤醒 settle 策略：锁屏常被误判为「全屏」，无限推迟会导致 Overlay 永久挂起。
/// </summary>
public static class ResumeSettlePolicy
{
    /// <summary>因 fullscreen 最多再等几轮（每轮约 3s）；超出后强制恢复。</summary>
    public const int MaxFullscreenDefers = 5;

    /// <summary>是否应强制 settle，即使当前仍报 fullscreen。</summary>
    public static bool ShouldForceSettleDespiteFullscreen(int fullscreenDeferCount) =>
        fullscreenDeferCount >= MaxFullscreenDefers;
}
