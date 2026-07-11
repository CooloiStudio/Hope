namespace Hope.Desktop.Ipc;

/// <summary>
/// IPC 连续连接失败计数：超过阈值只触发一次 Fatal，避免弹窗风暴。
/// 连接成功后重置。
/// </summary>
public sealed class IpcConnectFailureTracker
{
    private int _consecutiveFailures;
    private bool _fatalReported;

    public int MaxConnectFailures { get; set; } = 3;

    public int ConsecutiveFailures => _consecutiveFailures;

    public bool FatalReported => _fatalReported;

    public void OnConnected()
    {
        _consecutiveFailures = 0;
        _fatalReported = false;
    }

    /// <summary>
    /// 记录一次失败。若应触发 Fatal，返回 true 且写出原因（仅第一次）。
    /// </summary>
    public bool TryReportFatal(out string reason)
    {
        _consecutiveFailures++;
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
