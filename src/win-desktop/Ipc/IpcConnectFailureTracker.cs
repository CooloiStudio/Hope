namespace Hope.Desktop.Ipc;

/// <summary>
/// IPC 连续连接失败计数：超过阈值只触发一次 Fatal，避免弹窗风暴。
/// 连接成功后重置。休眠唤醒等可恢复窗口内可抑制 Fatal。
/// </summary>
public sealed class IpcConnectFailureTracker
{
    private int _consecutiveFailures;
    private bool _fatalReported;
    private long _suppressFatalUntilUtcTicks;

    public int MaxConnectFailures { get; set; } = 3;

    public int ConsecutiveFailures => _consecutiveFailures;

    public bool FatalReported => _fatalReported;

    /// <summary>在此 UTC 时刻之前，TryReportFatal 只计数不触发 Fatal（供休眠唤醒等可恢复场景）。</summary>
    public DateTime SuppressFatalUntilUtc
    {
        get => new(Interlocked.Read(ref _suppressFatalUntilUtcTicks), DateTimeKind.Utc);
        set => Interlocked.Exchange(ref _suppressFatalUntilUtcTicks, value.ToUniversalTime().Ticks);
    }

    public void OnConnected()
    {
        _consecutiveFailures = 0;
        _fatalReported = false;
    }

    /// <summary>重置失败计数与 Fatal 门闩（不改变抑制窗，除非调用方另行设置）。</summary>
    public void ResetFailures()
    {
        _consecutiveFailures = 0;
        _fatalReported = false;
    }

    /// <summary>
    /// 记录一次失败。若应触发 Fatal，返回 true 且写出原因（仅第一次）。
    /// 抑制窗内返回 false，以便后台继续重连。
    /// </summary>
    public bool TryReportFatal(out string reason)
    {
        _consecutiveFailures++;
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressFatalUntilUtcTicks))
        {
            reason = "";
            return false;
        }

        if (_consecutiveFailures > MaxConnectFailures && !_fatalReported)
        {
            _fatalReported = true;
            reason =
                $"无法连接到 hope-headless 核心进程（连续失败 {_consecutiveFailures} 次）。请检查后端是否被安全软件阻止，或尝试重启软件。";
            return true;
        }
        reason = "";
        return false;
    }
}
