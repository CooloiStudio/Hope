namespace Hope.Desktop.Services;

public enum UpdateStatus
{
    Idle,        // 空闲（未检查 / 已忽略）
    Checking,    // 正在检查
    UpToDate,    // 已是最新
    Available,   // 发现新版本（尚未下载）
    Downloading, // 正在下载
    Ready,       // 已下载并校验，待安装
    Failed,      // 检查或下载失败
}

/// <summary>
/// 更新流程协调器：统一管理「检查 → 提示 → 下载 → 安装」的状态机，
/// 供托盘（App）与设置窗口（关于页）共享与订阅。所有状态变更在 UI 线程回调。
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly UpdateService _service = new();
    private readonly Action _quitAction;
    private CancellationTokenSource? _cts;
    private string? _installerPath;
    private bool _busy;

    public UpdateCoordinator(Action quitAction) => _quitAction = quitAction;

    /// <summary>是否自动下载更新（来自全局设置，默认开）。关闭后仅检测并提示，不自动下载。</summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;
    public double DownloadProgress { get; private set; }
    public UpdateInfo? Latest { get; private set; }
    public string Message { get; private set; } = "";

    /// <summary>状态变更通知（已切回 UI 线程）。</summary>
    public event Action? StateChanged;

    /// <summary>发现可安装的新版本时触发一次（用于托盘气泡）；参数为版本展示文本。</summary>
    public event Action<string>? NewVersionAnnounced;

    public static Version CurrentVersion => UpdateService.CurrentVersion;

    /// <summary>检查更新。manual=true 时忽略「跳过此版本」并给出更明确的反馈。</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            SetState(UpdateStatus.Checking, "正在检查更新…");
            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            var info = await _service.CheckLatestAsync(_cts.Token).ConfigureAwait(false);
            if (info == null)
            {
                SetState(UpdateStatus.Failed, "无法连接到更新服务器，请稍后重试或手动前往发布页。");
                return;
            }

            Latest = info;
            if (info.LatestVersion <= CurrentVersion)
            {
                SetState(UpdateStatus.UpToDate, $"已是最新版本（v{CurrentVersion}）。");
                return;
            }

            // 自动检查时尊重「跳过此版本」；手动检查则始终提示。
            if (!manual)
            {
                var skipped = UpdatePrefs.Load().SkippedVersion;
                if (!string.IsNullOrEmpty(skipped) && skipped == info.Tag)
                {
                    SetState(UpdateStatus.Idle, $"已跳过版本 {info.Tag}。");
                    return;
                }
            }

            SetState(UpdateStatus.Available, $"发现新版本 {info.Tag}。");
            NewVersionAnnounced?.Invoke(info.Tag);

            if (AutoUpdateEnabled)
                await DownloadAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetState(UpdateStatus.Failed, "检查更新超时。");
        }
        catch (Exception ex)
        {
            DesktopLog.Error("UpdateCoordinator.CheckAsync failed", ex);
            SetState(UpdateStatus.Failed, $"检查更新出错：{ex.Message}");
        }
        finally { _busy = false; }
    }

    /// <summary>下载并校验安装包（手动「下载并更新」或自动更新时调用）。</summary>
    public async Task DownloadAsync()
    {
        if (Latest == null) return;
        var info = Latest;
        try
        {
            SetState(UpdateStatus.Downloading, $"正在下载 {info.Tag}…");
            DownloadProgress = 0;
            _cts ??= new CancellationTokenSource();

            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                RaiseOnUi();
            });

            _installerPath = await _service.DownloadInstallerAsync(info, progress, _cts.Token).ConfigureAwait(false);
            SetState(UpdateStatus.Ready, $"新版本 {info.Tag} 已就绪，可立即安装。");
            NewVersionAnnounced?.Invoke(info.Tag);
        }
        catch (OperationCanceledException)
        {
            SetState(UpdateStatus.Failed, "下载已取消。");
        }
        catch (Exception ex)
        {
            DesktopLog.Error("UpdateCoordinator.DownloadAsync failed", ex);
            SetState(UpdateStatus.Failed, $"下载失败：{ex.Message}");
        }
    }

    /// <summary>立即安装：拉起安装包静默就地升级并退出当前进程。需先处于 Ready 状态。</summary>
    public void InstallNow()
    {
        if (Status != UpdateStatus.Ready || string.IsNullOrEmpty(_installerPath)) return;
        UpdateService.LaunchInstallerAndExit(_installerPath, _quitAction);
    }

    /// <summary>跳过当前最新版本：记录后不再自动提示该版本。</summary>
    public void SkipCurrent()
    {
        if (Latest == null) return;
        var prefs = UpdatePrefs.Load();
        prefs.SkippedVersion = Latest.Tag;
        prefs.Save();
        SetState(UpdateStatus.Idle, $"已跳过版本 {Latest.Tag}。");
    }

    private void SetState(UpdateStatus status, string message)
    {
        Status = status;
        Message = message;
        DesktopLog.Info($"UpdateCoordinator: {status} - {message}");
        RaiseOnUi();
    }

    private void RaiseOnUi()
    {
        var app = System.Windows.Application.Current;
        if (app == null) { StateChanged?.Invoke(); return; }
        app.Dispatcher.BeginInvoke(() => StateChanged?.Invoke());
    }
}
