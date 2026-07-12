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
    private readonly TelemetryService? _telemetry;
    private CancellationTokenSource? _cts;
    private string? _installerPath;
    private bool _busy;

    public UpdateCoordinator(Action quitAction, TelemetryService? telemetry = null)
    {
        _quitAction = quitAction;
        _telemetry = telemetry;
    }

    /// <summary>是否自动下载更新（来自全局设置，默认开）。商店版忽略此项，仅提示前往 Microsoft Store。</summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>是否通过 Microsoft Store 分发（MSIX 包身份）。</summary>
    public static bool IsStoreManaged => InstallChannel.IsStoreManaged;

    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;
    public double DownloadProgress { get; private set; }
    public UpdateInfo? Latest { get; private set; }
    public string Message { get; private set; } = "";

    /// <summary>状态变更通知（已切回 UI 线程）。</summary>
    public event Action? StateChanged;

    /// <summary>发现可安装的新版本时触发一次（配置窗 Toast / 日志；不再用于托盘气球）。</summary>
    public event Action<string>? NewVersionAnnounced;

    public static Version CurrentVersion => UpdateService.CurrentVersion;

    /// <summary>检查更新。manual=true 时忽略「跳过此版本」并给出更明确的反馈。</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // 漏斗：手动检查上报一次（自动检查每天发生，量大故不计入，以可用/下载/安装等稀有事件为主）。
            if (manual)
                _telemetry?.TrackEvent("update_check", new Dictionary<string, object> { ["trigger"] = "manual" });

            SetState(UpdateStatus.Checking, "正在检查更新…");
            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            var info = await _service.CheckLatestAsync(_cts.Token).ConfigureAwait(false);
            if (info == null)
            {
                SetState(UpdateStatus.Failed, "无法连接到更新服务器，请稍后重试或手动前往发布页。");
                return;
            }

            // 本会话已在下载/已就绪同一（或更旧）版本时，跳过重复处理：
            // 否则「手动检查」与「启动后 25s 自动首检」等相继触发会把已下载好的版本再下一遍。
            if ((Status == UpdateStatus.Downloading || Status == UpdateStatus.Ready) &&
                Latest != null && info.LatestVersion <= Latest.LatestVersion)
            {
                DesktopLog.Info($"UpdateCoordinator: 跳过重复处理，已处于 {Status}（{Latest.Tag}）");
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

            SetState(UpdateStatus.Available, IsStoreManaged
                ? $"发现新版本 {info.Tag}，请前往 Microsoft Store 更新。"
                : $"发现新版本 {info.Tag}。");
            _telemetry?.TrackEvent("update_available", new Dictionary<string, object>
            {
                ["tag"] = info.Tag,
                ["source"] = info.Source,
                ["channel"] = IsStoreManaged ? "store" : "sideload",
            });
            NewVersionAnnounced?.Invoke(info.Tag);

            if (AutoUpdateEnabled && !IsStoreManaged)
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

    /// <summary>下载并校验安装包（手动「下载并更新」或自动更新时调用）。商店版改为打开 Microsoft Store。</summary>
    public async Task DownloadAsync()
    {
        if (Latest == null) return;
        if (IsStoreManaged)
        {
            if (InstallChannel.TryOpenMicrosoftStore())
                SetState(UpdateStatus.Available, $"新版本 {Latest.Tag} 请在 Microsoft Store 中安装。");
            else
                SetState(UpdateStatus.Failed, "无法打开 Microsoft Store，请从开始菜单打开「Microsoft Store」搜索 Hope 更新。");
            await Task.CompletedTask;
            return;
        }
        var info = Latest;
        try
        {
            SetState(UpdateStatus.Downloading, $"正在下载 {info.Tag}…");
            DownloadProgress = 0;
            // 不复用检查用的 CTS：检查那个带 2 分钟自动取消，复用会导致超 2 分钟后下载一进来就被取消。
            // 下载需独立、更宽松的超时（大文件 + 慢网络 + 多通道兜底）。
            _cts?.Dispose();
            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));

            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                RaiseOnUi();
            });

            _installerPath = await _service.DownloadInstallerAsync(info, progress, _cts.Token).ConfigureAwait(false);
            SetState(UpdateStatus.Ready, $"新版本 {info.Tag} 已就绪，可立即安装。");
            _telemetry?.TrackEvent("update_download", new Dictionary<string, object>
            {
                ["ok"] = "true",
                ["source"] = info.Source,
            });
            NewVersionAnnounced?.Invoke(info.Tag);
        }
        // 仅当我们主动取消（如退出）才算"已取消"；HttpClient.Timeout 抛的 TaskCanceledException
        // （token 未取消）属于网络超时/失败，应按"下载失败"提示并上报，而非误报"已取消"。
        catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
        {
            SetState(UpdateStatus.Failed, "下载已取消。");
        }
        catch (Exception ex)
        {
            DesktopLog.Error("UpdateCoordinator.DownloadAsync failed", ex);
            SetState(UpdateStatus.Failed, $"下载失败：{ex.Message}");
            // 仅上报阶段与异常类型，不含具体路径/URL 等可识别信息。
            _telemetry?.TrackEvent("update_failed", new Dictionary<string, object>
            {
                ["stage"] = "download",
                ["reason"] = ex.GetType().Name,
            });
        }
    }

    /// <summary>立即安装：侧载版拉起 Inno 安装包；商店版打开 Microsoft Store。</summary>
    public void InstallNow()
    {
        if (IsStoreManaged)
        {
            if (Latest != null)
                InstallChannel.TryOpenMicrosoftStore();
            return;
        }
        if (Status != UpdateStatus.Ready || string.IsNullOrEmpty(_installerPath)) return;
        _telemetry?.TrackEvent("update_install", new Dictionary<string, object>
        {
            ["tag"] = Latest?.Tag ?? "",
        });
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
