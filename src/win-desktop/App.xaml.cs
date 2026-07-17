using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Hope.Desktop.Ipc;
using Hope.Desktop.Overlay;
using Hope.Desktop.Services;
using Hope.Desktop.State;
using Hope.Desktop.Views;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Application = System.Windows.Application;

namespace Hope.Desktop;

/// <summary>
/// Desktop 入口：托管系统托盘、配置窗体、DWM 分段顶栏 Overlay 与 IPC 客户端，
/// 并监视拉起 Headless（文档 §3.3 / §5.3）。
/// </summary>
public partial class App : Application
{
    private IpcClient _ipc = null!;
    private readonly Dictionary<string, OverlayWindow> _overlays = new();
    private HeadlessSupervisor _supervisor = null!;
    private NotifyIcon? _tray;
    private ConfigWindow? _config;
    private readonly SessionState _session = new();
    /// <summary>Desktop 侧会话状态（供 ConfigWindow 等订阅）。</summary>
    internal SessionState Session => _session;
    /// <summary>已弹过到期通知的 taskId 集合；任务不再 expired 时清除，允许下个周期再次通知。</summary>
    private readonly HashSet<string> _notifiedTaskIds = new();

    private System.Threading.Mutex? _instanceMutex;

    private UpdateCoordinator _updates = null!;
    private DispatcherTimer? _updateTimer;
    /// <summary>休眠唤醒后至该时刻前跳过自动检查更新（避免补火日检 + 半开网络导致运行时故障）。</summary>
    private DateTime _autoUpdateCooldownUntilUtc = DateTime.MinValue;
    /// <summary>最近一次 PowerResume 时刻（UTC）；用于抑制唤醒后立即硬重建 Overlay。</summary>
    private DateTime _lastPowerResumeUtc = DateTime.MinValue;
    /// <summary>唤醒静默期截止（UTC）： settle 完成前拦截 Overlay/布局/状态广播等重 UI 路径。</summary>
    private DateTime _resumeQuiesceUntilUtc = DateTime.MinValue;
    private bool _pendingStateDuringQuiesce;
    private bool _pendingConnectionSnapshot;
    private bool _pendingEnsureOverlays;
    private StateMessage? _lastStateMessage;

    private readonly TelemetryService _telemetry = new();
    /// <summary>是否已上报过启动事件（首次读取到设置且允许时上报一次，避免重复）。</summary>
    private bool _telemetryStarted;
    /// <summary>是否已在本次启动对齐过「开机自启」配置与注册表实际状态（仅做一次）。</summary>
    private bool _autostartReconciled;

    private ScreenLayoutInfo? _currentScreenLayout;
    private DispatcherTimer? _layoutTimer;
    private DispatcherTimer? _displayChangeDebounce;
    private DispatcherTimer? _overlayResetDebounce;
    private DispatcherTimer? _postResumeSettleTimer;
    private ShellCompositionWatcher? _shellWatcher;
    private List<Segment> _lastStateSegments = new();
    private bool _lastCelebrate;

    /// <summary>启动后是否已决定是否自动打开设置窗（须 settings+tasks 均水合后再判）。</summary>
    private bool _startupConfigOpenDecided;

    private DispatcherTimer? _sessionSnapshotRetry;
    private int _sessionSnapshotAttempts;

    /// <summary>是否已经因致命错误进入退出流程，避免重复弹窗。</summary>
    private bool _fatalExiting;

    /// <summary>最近一次 IPC 后端恢复（杀孤儿+重连）时刻，用于防抖。</summary>
    private DateTime _lastIpcRecoveryUtc = DateTime.MinValue;
    private DispatcherTimer? _ipcHealthCheckTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Desktop 单实例，避免重复托盘 / 顶栏。
        _instanceMutex = new System.Threading.Mutex(true, @"Global\HopeDesktop", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        DesktopLog.Info($"Desktop starting OS={Environment.OSVersion.VersionString} " +
                        $"Mica={Wpf.Ui.Controls.WindowBackdrop.IsSupported(Wpf.Ui.Controls.WindowBackdropType.Mica)} " +
                        $"Acrylic={Wpf.Ui.Controls.WindowBackdrop.IsSupported(Wpf.Ui.Controls.WindowBackdropType.Acrylic)}");

        // 应用 Windows 11 Fluent 主题，跟随系统亮 / 暗（WPF-UI，文档 §5.3.2）。
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();

        _supervisor = new HeadlessSupervisor();
        _supervisor.FatalFailure += OnHeadlessFatalFailure;

        EnsureOverlays();

        _ipc = new IpcClient();
        _ipc.StateReceived += OnStateReceived;
        _ipc.SettingsReceived += OnSettingsReceived;
        _ipc.VersionReceived += OnVersionReceived;
        _ipc.ConnectionChanged += OnConnectionChanged;
        _ipc.TasksReceived += OnTasksReceived;
        _ipc.FatalDisconnected += OnIpcFatalDisconnected;
        _ipc.BackendRecoveryNeeded += OnIpcBackendRecoveryNeeded;
        _session.SettingsChanged += OnSessionSettingsChanged;

        // 当已存在可连接的 headless（如开发时手动启动）时，supervisor 不再重复拉起，避免 mutex 冲突。
        _supervisor.IsCoreReachable = () => _ipc.IsConnected;

        _supervisor.Start();
        _ipc.Start();

        // 初始化一次屏幕布局，避免启动时因 DPI/任务栏差异导致首帧位置错误。
        RefreshScreenLayout();

        _layoutTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _layoutTimer.Tick += (_, _) => RefreshScreenLayout();
        _layoutTimer.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        SetupTray();
        SetupUpdates();
        _shellWatcher = new ShellCompositionWatcher(() =>
            Dispatcher.BeginInvoke(() => ScheduleOverlayReset("ShellOrDwmChanged")));

        // 遥测不在此处发送：启动事件延迟到首次读取到全局设置（allowTelemetry）后再决定，
        // 见 OnSettingsReceived，确保用户取消勾选时不发送任何信息。
    }

    // 初始化自动更新：启动后延迟首检，之后每天检查一次；是否自动下载由全局设置控制。
    // 每日定时器同时承载匿名「日活心跳」app_active（受 allowTelemetry 开关控制）。
    private void SetupUpdates()
    {
        _updates = new UpdateCoordinator(() => Dispatcher.BeginInvoke(QuitAll), _telemetry);
        _updates.NewVersionAnnounced += OnNewVersionAnnounced;

        var initial = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        initial.Tick += (_, _) =>
        {
            initial.Stop();
            KickAutoUpdateCheck("startup");
        };
        initial.Start();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromDays(1) };
        _updateTimer.Tick += (_, _) =>
        {
            _telemetry.TrackEvent("app_active"); // 日活心跳：长期挂托盘不重启的用户也能被正确计入
            KickAutoUpdateCheck("daily");
        };
        _updateTimer.Start();
    }

    /// <summary>自动检查：线程池执行，并尊重唤醒冷却；异常不得冒泡到 DispatcherTimer。</summary>
    private void KickAutoUpdateCheck(string reason)
    {
        if (DateTime.UtcNow < _autoUpdateCooldownUntilUtc)
        {
            DesktopLog.Info($"Auto update check skipped ({reason}): post-resume cooldown");
            return;
        }

        SafeStartUpdateCheck(manual: false, reason);
    }

    /// <summary>手动/自动检查统一入口：不在 UI 定时器回调里直接启动 async 状态机。</summary>
    private void SafeStartUpdateCheck(bool manual, string reason)
    {
        DesktopLog.Info($"Update check queued manual={manual} reason={reason}");
        _ = Task.Run(async () =>
        {
            try
            {
                await _updates.CheckAsync(manual).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DesktopLog.Error($"Update check failed manual={manual} reason={reason}", ex);
                try { UpdateService.InvalidateHttpClients(); } catch { /* ignore */ }
            }
        });
    }

    /// <summary>休眠唤醒：冷却自动更新、重建 HttpClient、重置日检定时器，避免补火检查拖垮 UI。</summary>
    private void OnPowerResumed()
    {
        _lastPowerResumeUtc = DateTime.UtcNow;
        _autoUpdateCooldownUntilUtc = DateTime.UtcNow.AddMinutes(3);

        // 唤醒后管道常半开/短暂连不上；勿因此 FatalExit。给 headless 重拉与重连留时间。
        try { _ipc.NotePowerResumed(TimeSpan.FromMinutes(5)); } catch { /* ignore */ }

        // 静默期内禁止 Supervisor 乱拉起，避免与半开管道/互斥量抢跑（debug.log 20:02 类风暴）。
        try { _supervisor.PauseSpawning(TimeSpan.FromSeconds(12), "PowerResume"); }
        catch (Exception ex) { DesktopLog.Warn($"PauseSpawning: {ex.Message}"); }

        // DispatcherTimer 在休眠跨越到期点后，唤醒时常立刻补火；Stop/Start 把下次触发推到 +1 天。
        if (_updateTimer != null)
        {
            _updateTimer.Stop();
            _updateTimer.Start();
        }

        // 取消检查与 HttpClient 丢弃必须在后台做：UI 线程 Cancel/Dispose 半开请求曾打出 CLR Fatal（0x80131506）。
        var updates = _updates;
        _ = Task.Run(() =>
        {
            try { updates?.CancelForPowerResume(); }
            catch (Exception ex) { DesktopLog.Warn($"CancelForPowerResume: {ex.Message}"); }
            try { UpdateService.InvalidateHttpClients(); }
            catch (Exception ex) { DesktopLog.Warn($"InvalidateHttpClients: {ex.Message}"); }
        });

        DesktopLog.Info("Power resume: update cooldown 3m, HttpClient reset scheduled");
    }

    private void OnNewVersionAnnounced(string tag)
    {
        // 更新相关不再弹托盘气球；配置窗可见时由 StateChanged → RenderUpdateUi 发 Toast。
        DesktopLog.Info($"NewVersionAnnounced tag={tag} configVisible={_config?.IsVisible}");
    }

    // 连接建立后拉取一次全局设置，使进度条高度等即时生效，并上报当前主屏幕工作区尺寸。
    private void OnConnectionChanged(bool connected)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnConnectionChanged(connected));
            return;
        }

        DesktopLog.Info($"IPC connection changed connected={connected}");
        if (connected)
        {
            if (IsInResumeQuiesce())
            {
                DesktopLog.Info("IPC connection snapshot deferred (resume quiesce)");
                _pendingConnectionSnapshot = true;
                return;
            }

            _sessionSnapshotAttempts = 0;
            RequestSessionSnapshot();
            StartSessionSnapshotRetry();
        }
        else
        {
            StopSessionSnapshotRetry();
            // 断线后延迟探活：若仍不通则杀孤儿并强制重连（覆盖锁屏/唤醒后管道半死）。
            ScheduleIpcHealthCheck("disconnect");
        }
    }

    private void RequestSessionSnapshot()
    {
        _ipc.Send(new Command { Action = "getSettings" });
        _ipc.Send(new Command { Action = "getVersion" });
        _ipc.Send(new Command { Action = "listTasks" });
        SendScreenSize();
    }

    private void StartSessionSnapshotRetry()
    {
        StopSessionSnapshotRetry();
        _sessionSnapshotRetry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _sessionSnapshotRetry.Tick += OnSessionSnapshotRetryTick;
        _sessionSnapshotRetry.Start();
    }

    private void StopSessionSnapshotRetry()
    {
        if (_sessionSnapshotRetry == null) return;
        _sessionSnapshotRetry.Stop();
        _sessionSnapshotRetry.Tick -= OnSessionSnapshotRetryTick;
        _sessionSnapshotRetry = null;
    }

    private void OnSessionSnapshotRetryTick(object? sender, EventArgs e)
    {
        if (!_ipc.IsConnected)
        {
            StopSessionSnapshotRetry();
            return;
        }

        if (_session.SettingsHydrated && _session.TasksHydrated)
        {
            DesktopLog.Info("App session snapshot retry: satisfied");
            StopSessionSnapshotRetry();
            TryOpenStartupConfigWindow();
            return;
        }

        if (++_sessionSnapshotAttempts > 10)
        {
            DesktopLog.Warn($"App session snapshot retry: gave up attempts={_sessionSnapshotAttempts} " +
                            $"settingsHydrated={_session.SettingsHydrated} tasksHydrated={_session.TasksHydrated}");
            StopSessionSnapshotRetry();
            return;
        }

        DesktopLog.Info($"App session snapshot retry attempt={_sessionSnapshotAttempts}");
        _ipc.Send(new Command { Action = "getSettings" });
        _ipc.Send(new Command { Action = "listTasks" });
    }

    private void OnTasksReceived(List<TaskDto> tasks)
    {
        DesktopLog.Info($"App.OnTasksReceived count={tasks.Count}");
        Dispatcher.BeginInvoke(() =>
        {
            _session.ApplyTasks(tasks);
            SyncConfigWindowFromSession();
            UpdateTrayTooltipFromTasks();
            TryOpenStartupConfigWindow();
        });
    }

    private void OnHeadlessFatalFailure(string reason)
    {
        Dispatcher.BeginInvoke(() => FatalExit("Hope · 后端启动失败", reason));
    }

    private void OnIpcFatalDisconnected(string reason)
    {
        // 休眠唤醒冷却期内偶发连不上：只记日志，不弹窗退出（IpcClient 会继续重连）。
        if (DateTime.UtcNow < _lastPowerResumeUtc.AddMinutes(5))
        {
            DesktopLog.Warn($"IPC fatal suppressed post-resume: {reason}");
            try { _ipc.NotePowerResumed(TimeSpan.FromMinutes(5)); } catch { /* ignore */ }
            RecoverIpcBackend("FatalSuppressedPostResume");
            return;
        }

        Dispatcher.BeginInvoke(() => FatalExit("Hope · 通讯异常", reason));
    }

    private void OnIpcBackendRecoveryNeeded(string reason)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnIpcBackendRecoveryNeeded(reason));
            return;
        }

        RecoverIpcBackend(reason);
    }

    /// <summary>断线后延迟检查：仍未连通则走 RecoverIpcBackend。</summary>
    private void ScheduleIpcHealthCheck(string reason)
    {
        _ipcHealthCheckTimer ??= new DispatcherTimer();
        _ipcHealthCheckTimer.Stop();
        _ipcHealthCheckTimer.Interval = TimeSpan.FromSeconds(2.5);
        _ipcHealthCheckTimer.Tick -= OnIpcHealthCheckTick;
        _ipcHealthCheckTimer.Tag = reason;
        _ipcHealthCheckTimer.Tick += OnIpcHealthCheckTick;
        _ipcHealthCheckTimer.Start();
        DesktopLog.Info($"IPC health check scheduled reason={reason}");
    }

    private void OnIpcHealthCheckTick(object? sender, EventArgs e)
    {
        _ipcHealthCheckTimer?.Stop();
        var reason = _ipcHealthCheckTimer?.Tag as string ?? "health-check";
        if (_ipc.IsConnected)
        {
            DesktopLog.Info($"IPC health check ok reason={reason}");
            return;
        }

        DesktopLog.Warn($"IPC health check failed reason={reason}, recovering backend");
        RecoverIpcBackend(reason);
    }

    /// <summary>
    /// 唤醒/解锁后管道半死的恢复：暂停 Supervisor → 拆管立即重连 → 杀掉占 mutex 的孤儿 headless。
    /// Supervisor 暂停结束后会干净拉起；防抖避免重连风暴。
    /// </summary>
    private void RecoverIpcBackend(string reason)
    {
        if (DateTime.UtcNow < _lastIpcRecoveryUtc.AddSeconds(15))
        {
            DesktopLog.Info($"IPC backend recovery deferred reason={reason}");
            return;
        }

        _lastIpcRecoveryUtc = DateTime.UtcNow;
        DesktopLog.Info($"IPC backend recovery begin reason={reason}");

        try { _ipc.NotePowerResumed(TimeSpan.FromMinutes(5)); } catch { /* ignore */ }
        try { _supervisor.PauseSpawning(TimeSpan.FromSeconds(8), reason); }
        catch (Exception ex) { DesktopLog.Warn($"PauseSpawning: {ex.Message}"); }

        _ = Task.Run(() =>
        {
            try
            {
                try { _ipc.RequestReconnect(reason); }
                catch (Exception ex) { DesktopLog.Warn($"RequestReconnect: {ex.Message}"); }

                // 稍等拆管生效，再杀孤儿，避免新旧进程同时争 mutex。
                Thread.Sleep(300);
                var killed = HeadlessSupervisor.KillOrphanHeadlessProcesses(reason);
                DesktopLog.Info($"IPC backend recovery done killed={killed} reason={reason}");
            }
            catch (Exception ex)
            {
                DesktopLog.Warn($"IPC backend recovery failed: {ex.Message}");
            }
        });
    }

    private void FatalExit(string caption, string message)
    {
        if (_fatalExiting) return;
        _fatalExiting = true;
        DesktopLog.Error($"FatalExit: {caption} - {message}");
        System.Windows.MessageBox.Show(
            message + "\n\n点击确定后将关闭 Hope。",
            caption,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        QuitAll();
    }

    // 仅上报屏幕尺寸，使用独立的 screenSize 命令；切勿走 updateSettings，
    // 否则 SettingsDto 的默认值（barHeightPx=4、barPosition=top、expiredBehaviors 等）
    // 会经服务端 mergeSettings 把用户已保存的全局设置覆盖为默认值（启动即丢设置）。
    private void SendScreenSize()
    {
        var rect = _currentScreenLayout?.EffectiveArea(_session.OverlayBarPosition) ?? SystemParameters.WorkArea;
        _ipc.Send(new Command
        {
            Action = "screenSize",
            Settings = new SettingsDto
            {
                ScreenWidth = rect.Width,
                ScreenHeight = rect.Height
            }
        });
    }


    /// <summary>IPC 写入 Session 后，若设置窗已打开则强制同步 UI（弥补订阅前已水合或 revision 跳过）。</summary>
    private void SyncConfigWindowFromSession()
    {
        if (_config is not { IsVisible: true }) return;
        _config.HydrateFromSession(force: true);
    }

    /// <summary>settings 与 tasks 均水合后，再按「启动打开设置」或空任务列表决定是否开窗。</summary>
    private void TryOpenStartupConfigWindow()
    {
        if (_startupConfigOpenDecided) return;
        if (!_session.SettingsHydrated || !_session.TasksHydrated) return;

        _startupConfigOpenDecided = true;
        var settings = _session.Settings;
        bool showAtRuntime = settings?.ShowConfigAtRuntime == true;
        bool noTasks = _session.Tasks.Count == 0;
        if (showAtRuntime || noTasks)
        {
            DesktopLog.Info($"TryOpenStartupConfigWindow showAtRuntime={showAtRuntime} noTasks={noTasks}");
            ShowConfig();
        }
    }

    /// <summary>检测主屏布局变化；变化时同步后端尺寸并让各 Overlay 立即重算位置。</summary>
    private void RefreshScreenLayout()
    {
        if (IsInResumeQuiesce())
        {
            DesktopLog.Info("Screen layout refresh skipped (resume quiesce)");
            return;
        }

        var layout = ScreenLayoutService.GetCurrent();
        bool changed = _currentScreenLayout == null ||
                       !RectsEqual(_currentScreenLayout.WorkArea, layout.WorkArea) ||
                       !RectsEqual(_currentScreenLayout.Bounds, layout.Bounds) ||
                       _currentScreenLayout.TaskbarEdge != layout.TaskbarEdge ||
                       _currentScreenLayout.TaskbarAutoHide != layout.TaskbarAutoHide ||
                       _currentScreenLayout.HasFullScreenOnPrimary != layout.HasFullScreenOnPrimary;
        if (!changed) return;

        _currentScreenLayout = layout;
        DesktopLog.Info($"Screen layout changed workArea={layout.WorkArea} bounds={layout.Bounds} " +
                        $"edge={layout.TaskbarEdge} autoHide={layout.TaskbarAutoHide} fullScreen={layout.HasFullScreenOnPrimary}");

        // 逐个 Overlay 打断点：RefreshLayout 会触碰 GDI/位图，唤醒/熄屏后若 DWM 未就绪可能打出
        // 不可捕获的 CLR Fatal（0x80131506）。最后一条存活日志即可定位崩溃在哪个 Overlay/步骤。
        foreach (var kv in _overlays)
        {
            DesktopLog.Info($"RefreshScreenLayout: refreshing overlay pos={kv.Key}");
            kv.Value.ScreenLayout = layout;
            kv.Value.RefreshLayout();
        }

        if (_ipc.IsConnected)
        {
            DesktopLog.Info("RefreshScreenLayout: sending screen size");
            SendScreenSize();
        }
        DesktopLog.Info("RefreshScreenLayout: done");
    }

    /// <summary>读取 HKCU Run\Hope 是否存在，判断系统层面是否已配置开机自启。</summary>
    private static bool IsAutostartInRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            return key?.GetValue("Hope") != null;
        }
        catch { return false; }
    }

    private static bool RectsEqual(System.Windows.Rect a, System.Windows.Rect b)
    {
        return Math.Abs(a.X - b.X) < 0.01 &&
               Math.Abs(a.Y - b.Y) < 0.01 &&
               Math.Abs(a.Width - b.Width) < 0.01 &&
               Math.Abs(a.Height - b.Height) < 0.01;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        DesktopLog.Info("DisplaySettingsChanged received");
        Dispatcher.BeginInvoke(() => ScheduleRefreshScreenLayout("DisplaySettingsChanged"));
    }

    // 会话切换（锁屏/解锁/远程连接等）常与「看似唤醒」的显示器熄屏、RDP 混淆；
    // 单独记录以便与 PowerModeChanged 交叉对照，判断崩溃/异常发生的真实场景。
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        DesktopLog.Info($"SessionSwitch reason={e.Reason}");
        if (e.Reason != SessionSwitchReason.SessionUnlock) return;

        // 解锁后管道常已半死但进程仍占 mutex；延迟探活，不通则恢复。
        void OnUnlock()
        {
            try { _ipc.NotePowerResumed(TimeSpan.FromMinutes(5)); } catch { /* ignore */ }
            ScheduleIpcHealthCheck("SessionUnlock");
        }

        if (Dispatcher.CheckAccess())
            OnUnlock();
        else
            Dispatcher.BeginInvoke(OnUnlock);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // 记录所有模式（含 Suspend/StatusChange）：若唤醒后崩溃却无此行，说明 OS 根本未投递电源事件，
        // 崩溃更可能来自熄屏/DWM 重置而非真正的 S3/S0 唤醒（用于区分 202607161531.log 类现象）。
        DesktopLog.Info($"PowerModeChanged mode={e.Mode} resumeQuiesce={IsInResumeQuiesce()}");
        if (e.Mode != PowerModes.Resume) return;

        // 必须尽快停掉 CompositionTarget / 布局定时器：Background 延迟回调来不及，
        // 下一帧动图 LockBits 就可能在 DWM 未就绪时打出 CLR Fatal（0x80131506）。
        void BeginResumeSequence()
        {
            try
            {
                DesktopLog.Info("Power resume: suspend rendering, defer soft refresh");
                BeginResumeQuiesce(TimeSpan.FromSeconds(10));
                SuspendUiForPowerResume();
                OnPowerResumed();
                SchedulePostResumeSettle(delayMs: 10000);
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Power resume sequence failed", ex);
            }
        }

        if (Dispatcher.CheckAccess())
            BeginResumeSequence();
        else
            Dispatcher.BeginInvoke(BeginResumeSequence, DispatcherPriority.Send);
    }

    private bool IsInResumeQuiesce() => DateTime.UtcNow < _resumeQuiesceUntilUtc;

    private void BeginResumeQuiesce(TimeSpan duration)
    {
        _resumeQuiesceUntilUtc = DateTime.UtcNow.Add(duration);
        _pendingStateDuringQuiesce = false;
        _pendingConnectionSnapshot = false;
        _pendingEnsureOverlays = false;
    }

    /// <summary>唤醒瞬间：停布局探测与 Overlay 动图/悬停，避免立刻触碰 GDI/合成。</summary>
    private void SuspendUiForPowerResume()
    {
        try { _layoutTimer?.Stop(); } catch { /* ignore */ }
        try { _displayChangeDebounce?.Stop(); } catch { /* ignore */ }
        try { _overlayResetDebounce?.Stop(); } catch { /* ignore */ }
        try { StopSessionSnapshotRetry(); } catch { /* ignore */ }
        foreach (var overlay in _overlays.Values.ToList())
        {
            try { overlay.SuspendRendering(); }
            catch (Exception ex) { DesktopLog.Warn($"SuspendRendering: {ex.Message}"); }
        }
    }

    /// <summary>唤醒稳定后再恢复渲染并软刷新状态。</summary>
    private void SchedulePostResumeSettle(int delayMs)
    {
        _postResumeSettleTimer ??= new DispatcherTimer();
        _postResumeSettleTimer.Stop();
        _postResumeSettleTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _postResumeSettleTimer.Tick -= OnPostResumeSettleTick;
        _postResumeSettleTimer.Tick += OnPostResumeSettleTick;
        _postResumeSettleTimer.Start();
    }

    private void OnPostResumeSettleTick(object? sender, EventArgs e)
    {
        _postResumeSettleTimer?.Stop();
        try
        {
            DesktopLog.Info("Power resume settle: resume rendering + soft refresh");
            if (_currentScreenLayout?.HasFullScreenOnPrimary == true)
            {
                DesktopLog.Info("Power resume settle deferred (fullscreen)");
                _resumeQuiesceUntilUtc = DateTime.UtcNow.AddSeconds(3);
                SchedulePostResumeSettle(delayMs: 3000);
                return;
            }

            FlushResumeQuiescePending();

            foreach (var overlay in _overlays.Values.ToList())
            {
                try { overlay.ResumeRendering(); }
                catch (Exception ex) { DesktopLog.Warn($"ResumeRendering: {ex.Message}"); }
            }

            try { _layoutTimer?.Start(); } catch { /* ignore */ }
            if (!_ipc.IsConnected)
            {
                DesktopLog.Info("Power resume settle: IPC not connected, recovering backend");
                RecoverIpcBackend("PowerResumeSettle");
            }
            else
            {
                RequestOverlayStateRefresh();
            }
            ScheduleRefreshScreenLayout("PowerResumeSettle", delayMs: 400);
        }
        catch (Exception ex)
        {
            DesktopLog.Error("Power resume settle failed", ex);
        }
    }

    /// <summary>静默期结束后回放唤醒期间积压的 Overlay/会话工作。</summary>
    private void FlushResumeQuiescePending()
    {
        _resumeQuiesceUntilUtc = DateTime.MinValue;

        if (_pendingEnsureOverlays)
        {
            _pendingEnsureOverlays = false;
            if (_session.OverlayAllFour || _lastCelebrate)
                EnsureOverlays(forceAllFour: true);
            else if (_session.OverlayAdvancedPosition)
            {
                var positions = _lastStateSegments
                    .Select(s => _session.ResolveSegmentPosition(s.Position))
                    .Distinct();
                EnsureOverlays(positions);
            }
            else
                EnsureOverlays();
        }

        if (_pendingConnectionSnapshot && _ipc.IsConnected)
        {
            _pendingConnectionSnapshot = false;
            RequestSessionSnapshot();
        }
        else
            _pendingConnectionSnapshot = false;

        // EnsureOverlays 可能新建窗口（尚未 Suspend）；派发状态前统一挂起，再由 settle 批量 Resume。
        foreach (var overlay in _overlays.Values.ToList())
        {
            try { overlay.SuspendRendering(); }
            catch (Exception ex) { DesktopLog.Warn($"FlushResumeQuiesce SuspendRendering: {ex.Message}"); }
        }

        if (_pendingStateDuringQuiesce && _lastStateMessage != null)
        {
            _pendingStateDuringQuiesce = false;
            DispatchState(_lastStateMessage, _lastCelebrate);
        }
        else
            _pendingStateDuringQuiesce = false;
    }

    /// <summary>按当前墙钟向 Headless 索取一帧顶栏状态并刷新 Overlay（休眠唤醒后进度条/图片停滞）。</summary>
    private void RequestOverlayStateRefresh()
    {
        var overlays = _overlays.Values.ToList();
        foreach (var overlay in overlays)
            overlay.InvalidateVisualCache();

        if (!_ipc.IsConnected)
        {
            DesktopLog.Warn("App.RequestOverlayStateRefresh skipped: IPC disconnected");
            return;
        }

        DesktopLog.Info($"App.RequestOverlayStateRefresh requesting snapshots overlays={overlays.Count}");
        // 仅请求后端快照，不在唤醒路径本地重放上一次状态，避免 UI 线程在恢复期做重渲染。
        _ipc.Send(new Command { Action = "requestState" });
        _ipc.Send(new Command { Action = "listTasks" });
        _ipc.Send(new Command { Action = "getSettings" });
        _ipc.Send(new Command { Action = "getVersion" });
    }

    /// <summary>合并短时间内的多次屏幕布局变更（唤醒、分辨率切换等），减轻 UI 线程突发负载。</summary>
    private void ScheduleRefreshScreenLayout(string reason, int delayMs = 400)
    {
        if (IsInResumeQuiesce())
        {
            DesktopLog.Info($"Screen layout refresh deferred reason={reason} (resume quiesce)");
            return;
        }

        _displayChangeDebounce ??= new DispatcherTimer();
        _displayChangeDebounce.Interval = TimeSpan.FromMilliseconds(delayMs);
        _displayChangeDebounce.Stop();
        _displayChangeDebounce.Tick -= OnDisplayChangeDebounceTick;
        _displayChangeDebounce.Tag = reason;
        _displayChangeDebounce.Tick += OnDisplayChangeDebounceTick;
        _displayChangeDebounce.Start();
    }

    private void OnDisplayChangeDebounceTick(object? sender, EventArgs e)
    {
        _displayChangeDebounce?.Stop();
        var reason = _displayChangeDebounce?.Tag as string ?? "unknown";
        DesktopLog.Info($"Screen layout refresh scheduled reason={reason}");
        RefreshScreenLayout();
    }

    private void OnSessionSettingsChanged(SettingsDto s)
    {
        if (_updates != null) _updates.AutoUpdateEnabled = s.AutoUpdate;
        EnsureOverlays();
    }

    private void OnSettingsReceived(SettingsDto s)
    {
        DesktopLog.Info($"App.OnSettingsReceived barHeightPx={s.BarHeightPx} showConfigAtRuntime={s.ShowConfigAtRuntime} barPosition={s.BarPosition} barDirection={s.BarDirection}");
        Dispatcher.BeginInvoke(() =>
        {
            _session.ApplySettings(s);

            // 遥测开关：用户取消勾选后关闭上报，不再发送任何事件。
            _telemetry.Enabled = s.AllowTelemetry;
            if (!_telemetryStarted && s.AllowTelemetry)
            {
                _telemetryStarted = true;
                _telemetry.TrackEvent("app_started");
            }

            // 开机自启对齐：安装程序可能已写入注册表自启项，而配置仍为关闭，
            // 导致 UI 与系统实际不一致，且任意改设置都会误删该自启项。
            // 首次读到设置时以注册表实际状态为准，回写配置一次。
            if (!_autostartReconciled)
            {
                _autostartReconciled = true;
                bool regOn = IsAutostartInRegistry();
                if (regOn != s.Autostart)
                {
                    DesktopLog.Info($"Autostart reconcile: registry={regOn} config={s.Autostart} → 以注册表为准对齐配置");
                    s.Autostart = regOn;
                    _ipc.Send(new Command { Action = "updateSettings", Settings = s });
                }
            }

            SyncConfigWindowFromSession();
            TryOpenStartupConfigWindow();
        });
    }

    private void OnVersionReceived(string version)
    {
        // 托盘 tooltip 的版本号取自桌面端程序自身（见 TrayHeader），此处仅记录后端版本。
        DesktopLog.Info($"App.OnVersionReceived headlessVersion={version}");
    }

    // 托盘标题行：Hope·盼头 v<桌面端版本>（版本号取自程序配置/程序集）。
    private static string TrayHeader()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        string ver = v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
        return $"Hope·盼头 v{ver}";
    }

    // 根据任务列表刷新托盘 tooltip：仅进行中/已到期（不含未开始与用户手动完成的任务）。
    // 受 NotifyIcon.Text 长度限制（≤127），超出时截断并以省略行提示。
    private void UpdateTrayTooltipFromTasks()
    {
        if (_tray == null) return;

        var now = DateTimeOffset.Now;
        var rows = _session.Tasks
            .Where(t => !IsTaskCompleted(t))
            .Select(TaskRow.From)
            .Where(r => TaskSchedule.HasStarted(r.Type, r.StartTs, r.EndTs, r.CreatedAt, now))
            .OrderBy(r => TaskSchedule.Percent(r.Type, r.StartTs, r.EndTs, r.CreatedAt, now))
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        const int maxLen = 127;
        const string nl = "\r\n";
        var text = new StringBuilder(TrayHeader());
        if (rows.Count > 0)
        {
            int nameWidth = rows.Max(r => DisplayWidth(r.Name ?? ""));
            text.Append(nl).Append("————");
            foreach (var row in rows)
            {
                string status = TaskSchedule.GetTrayStatusLabel(row, now);
                string line = $"{PadToWidth(row.Name ?? "", nameWidth)} {status}";
                if (text.Length + nl.Length + line.Length > maxLen - 3)
                {
                    text.Append(nl).Append('…');
                    break;
                }
                text.Append(nl).Append(line);
            }
        }

        string final = text.ToString();
        if (final.Length > maxLen) final = final[..maxLen];
        _tray.Text = final;
    }

    private static bool IsTaskCompleted(TaskDto t) =>
        t.Completed || string.Equals(t.Status, "completed", StringComparison.OrdinalIgnoreCase);

    // 显示宽度：中文/全角字符按 2 列，半角字符按 1 列（近似 wcwidth）。
    private static int DisplayWidth(string s)
    {
        int w = 0;
        foreach (var ch in s) w += IsWideChar(ch) ? 2 : 1;
        return w;
    }

    private static bool IsWideChar(char c)
    {
        return c >= 0x1100 && (
            c <= 0x115F ||                          // Hangul Jamo
            (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) || // CJK 部首…彝文
            (c >= 0xAC00 && c <= 0xD7A3) ||         // Hangul 音节
            (c >= 0xF900 && c <= 0xFAFF) ||         // CJK 兼容表意
            (c >= 0xFE30 && c <= 0xFE4F) ||         // CJK 兼容形式
            (c >= 0xFF00 && c <= 0xFF60) ||         // 全角 ASCII / 标点
            (c >= 0xFFE0 && c <= 0xFFE6));          // 全角符号
    }

    // 用全角空格（U+3000，占 2 列）+ 半角空格（占 1 列）把名称补齐到目标显示宽度。
    private static string PadToWidth(string name, int targetWidth)
    {
        int deficit = targetWidth - DisplayWidth(name);
        if (deficit <= 0) return name;
        int full = deficit / 2;
        int half = deficit % 2;
        return name + new string('\u3000', full) + new string(' ', half);
    }

    private void OnStateReceived(StateMessage msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var all = msg.Segments ?? new List<Segment>();
            _lastStateSegments = all;
            _lastStateMessage = msg;
            bool celebrate = IsCelebrateActive(all);
            _lastCelebrate = celebrate;

            if (IsInResumeQuiesce())
            {
                DesktopLog.Info("State broadcast deferred (resume quiesce)");
                _pendingStateDuringQuiesce = true;
                UpdateTrayTooltipFromTasks();
                return;
            }

            if (_session.OverlayAllFour || celebrate)
                EnsureOverlays(forceAllFour: true);
            else if (!_session.OverlayAdvancedPosition)
                EnsureOverlays();
            DispatchState(msg, celebrate);
            UpdateTrayTooltipFromTasks();

            // 清除已不在当前到期列表中的 taskId，允许下个周期再次通知
            var currentExpiredIds = new HashSet<string>(
                all.Where(s => s.Expired).Select(s => s.TaskId)
            );
            _notifiedTaskIds.IntersectWith(currentExpiredIds);

            if (msg.Expired != null)
                foreach (var ev in msg.Expired) HandleExpired(ev);
        });
    }

    private static bool IsCelebrateActive(List<Segment> segments) =>
        segments.Any(s => s.Expired && s.Behaviors != null && s.Behaviors.Contains("celebrate"));

    /// <summary>根据当前全局设置确保创建或销毁对应 OverlayWindow。</summary>
    private void EnsureOverlays(IEnumerable<string>? activePositions = null, bool forceAllFour = false)
    {
        if (IsInResumeQuiesce())
        {
            DesktopLog.Info("EnsureOverlays deferred (resume quiesce)");
            _pendingEnsureOverlays = true;
            return;
        }

        var wanted = new HashSet<string>();
        if (_session.OverlayAllFour || forceAllFour)
        {
            wanted.Add(OverlayWindow.PositionTop);
            wanted.Add(OverlayWindow.PositionRight);
            wanted.Add(OverlayWindow.PositionBottom);
            wanted.Add(OverlayWindow.PositionLeft);
        }
        else if (_session.OverlayAdvancedPosition)
        {
            wanted.Add(_session.OverlayBarPosition);
            if (activePositions != null)
            {
                foreach (var p in activePositions)
                {
                    if (!string.IsNullOrWhiteSpace(p))
                        wanted.Add(p);
                }
            }
        }
        else
        {
            wanted.Add(_session.OverlayBarPosition);
        }

        foreach (var pos in _overlays.Keys.ToList())
        {
            if (!wanted.Contains(pos))
            {
                _overlays[pos].ForceClose();
                _overlays.Remove(pos);
            }
        }

        foreach (var pos in wanted)
        {
            var dir = _session.DirectionForPosition(pos);
            if (!_overlays.ContainsKey(pos))
            {
                _overlays[pos] = new OverlayWindow
                {
                    Position = pos,
                    Direction = dir,
                    ScreenLayout = _currentScreenLayout,
                };
                DesktopLog.Info($"EnsureOverlays created pos={pos} direction={dir}");
            }
            else
            {
                var oldDir = _overlays[pos].Direction;
                _overlays[pos].Position = pos;
                _overlays[pos].Direction = dir;
                if (oldDir != dir)
                    DesktopLog.Info($"EnsureOverlays updated pos={pos} direction={oldDir}->{dir}");
            }
        }
    }

    private void DispatchState(StateMessage msg, bool celebrate)
    {
        ResolveSegmentImageHeights(msg.Segments);
        var all = celebrate && !_session.OverlayAllFour ? ExpandForCelebrate(msg.Segments ?? new List<Segment>()) : (msg.Segments ?? new List<Segment>());
        if (celebrate && !_session.OverlayAllFour)
            ResolveSegmentImageHeights(all);
        if (_session.OverlayAdvancedPosition && !_session.OverlayAllFour && !celebrate)
        {
            var positions = all
                .Select(s => _session.ResolveSegmentPosition(s.Position))
                .Distinct();
            EnsureOverlays(positions);
        }
        var groups = all.GroupBy(s => _session.ResolveSegmentPosition(s.Position))
                        .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (pos, overlay) in _overlays)
        {
            bool hasSegments = groups.TryGetValue(pos, out var segs) && segs.Count > 0;
            var windowMsg = new StateMessage
            {
                Version = msg.Version,
                Visible = msg.Visible && hasSegments,
                State = msg.State,
                Segments = segs ?? new List<Segment>(),
            };
            overlay.UpdateState(windowMsg, _session.OverlayBarHeightPx);
        }
    }

    /// <summary>按会话设置把 segment.ImageMaxSize 解析为最终展示像素（原地写入）。</summary>
    private void ResolveSegmentImageHeights(List<Segment>? segments)
    {
        if (segments == null) return;
        foreach (var s in segments)
            s.ImageMaxSize = _session.ResolveImageMaxSize(s.ImageMaxSize);
    }

    // 庆祝模式（非四边环绕）：把「触发庆祝的到期段」复制到四条边做满填 + 闪烁。
    // - 主进度条（home，自身 Position，缺省取全局位）：保持原子区间布局不变，图片只挂在此处。
    // - 其余三条边：整条铺满完成色（BarStart=0/BarEnd=100/FillEnd=100），与主条同频闪烁，不挂图。
    // 未完成 / 非庆祝段原样保留——仍只在自身位置渲染、带自己的图，不复制到其它边。
    private List<Segment> ExpandForCelebrate(List<Segment> segments)
    {
        var sides = new[] { OverlayWindow.PositionTop, OverlayWindow.PositionRight, OverlayWindow.PositionBottom, OverlayWindow.PositionLeft };
        var expanded = new List<Segment>(segments.Count);
        foreach (var s in segments)
        {
            bool isCelebrate = s.Expired && s.Behaviors != null && s.Behaviors.Contains("celebrate");
            if (!isCelebrate)
            {
                expanded.Add(s); // 未完成/非庆祝段：保持原位置与图片
                continue;
            }

            string home = _session.ResolveSegmentPosition(s.Position);
            foreach (var side in sides)
            {
                bool isHome = side == home;
                expanded.Add(new Segment
                {
                    TaskId = s.TaskId,
                    Name = s.Name,
                    Color = s.Color,
                    Gif = isHome ? s.Gif : null, // 图片只在任务到期位置
                    ImageMaxSize = s.ImageMaxSize,
                    // 主条保留原子区间；其余三边整条铺满完成色。
                    BarStart = isHome ? s.BarStart : 0.0,
                    BarEnd = isHome ? s.BarEnd : 100.0,
                    Percent = isHome ? s.Percent : 100.0,
                    FillEnd = isHome ? s.FillEnd : 100.0,
                    EndAt = s.EndAt,
                    Expired = s.Expired,
                    Behaviors = s.Behaviors,
                    Position = side,
                    Direction = s.Direction,
                    ImageRotation = s.ImageRotation,
                });
            }
        }
        return expanded;
    }

    private void HandleExpired(ExpiredEvent ev)
    {
        // 去重：同一到期周期只弹一次通知。
        if (_notifiedTaskIds.Contains(ev.TaskId)) return;
        _notifiedTaskIds.Add(ev.TaskId);

        // 一次性提醒：notify 弹气球。keep/hide/blink 为持续表现，由 Overlay 依据 Segment 状态驱动。
        if (ev.Behaviors != null && ev.Behaviors.Contains("notify"))
            _tray?.ShowBalloonTip(5000, "Hope · 任务到期", $"「{ev.Name}」已到达截止时间", ToolTipIcon.Info);
    }

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置(&S)", null, (_, _) => ShowConfig());
        menu.Items.Add("检查更新(&U)", null, (_, _) => SafeStartUpdateCheck(manual: true, "tray"));
        var refreshItem = new ToolStripMenuItem("刷新进度条(&R)")
        {
            ToolTipText = "重建进度条窗口；同时将进行中的即时任务起点设为当前时刻（定时任务不变）",
        };
        refreshItem.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RefreshProgressBarManual();
            _config?.NotifyProgressBarRefreshed();
        });
        menu.Items.Add(refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出(&Q)", null, (_, _) => QuitAll());

        _tray = new NotifyIcon
        {
            Icon = CreateTrayIconSafe(),
            Visible = true,
            Text = TrayHeader(),
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowConfig();
    }

    /// <summary>
    /// 用户主动「刷新进度条」：先重置进行中即时任务起点，再销毁重建 Overlay。
    /// </summary>
    internal void RefreshProgressBarManual()
    {
        try
        {
            var now = DateTimeOffset.Now;
            int resetCount = 0;
            foreach (var task in _session.Tasks)
            {
                if (!ProgressBarRefresh.ShouldResetInstantStart(task, now)) continue;
                _ipc.Send(new Command
                {
                    Action = "updateTask",
                    Task = ProgressBarRefresh.WithInstantStartReset(task, now),
                });
                resetCount++;
            }
            DesktopLog.Info($"RefreshProgressBarManual instantReset={resetCount}");
            ResetOverlays("manual");
        }
        catch (Exception ex)
        {
            DesktopLog.Error("RefreshProgressBarManual failed", ex);
        }
    }

    /// <summary>防抖后执行 Overlay 销毁重建（系统事件路径不改任务时间）。</summary>
    private void ScheduleOverlayReset(string reason, int delayMs = 500)
    {
        if (IsInResumeQuiesce())
        {
            var waitMs = (int)Math.Ceiling((_resumeQuiesceUntilUtc - DateTime.UtcNow).TotalMilliseconds);
            delayMs = Math.Max(delayMs, Math.Max(500, waitMs + 200));
            DesktopLog.Info($"ResetOverlays deferred reason={reason} waitMs={delayMs} (resume quiesce)");
        }

        _overlayResetDebounce ??= new DispatcherTimer();
        _overlayResetDebounce.Interval = TimeSpan.FromMilliseconds(delayMs);
        _overlayResetDebounce.Stop();
        _overlayResetDebounce.Tick -= OnOverlayResetDebounceTick;
        _overlayResetDebounce.Tag = reason;
        _overlayResetDebounce.Tick += OnOverlayResetDebounceTick;
        _overlayResetDebounce.Start();
    }

    private void OnOverlayResetDebounceTick(object? sender, EventArgs e)
    {
        _overlayResetDebounce?.Stop();
        var reason = _overlayResetDebounce?.Tag as string ?? "unknown";

        // 唤醒后短窗内 DWM 抖动 + ShellCompositionWatcher 也可能触发硬重建；延后并避开伪全屏。
        // 与 post-resume settle（10s）对齐，避免静默期结束后立刻硬销毁窗口。
        var sinceResume = DateTime.UtcNow - _lastPowerResumeUtc;
        if (sinceResume >= TimeSpan.Zero && sinceResume < TimeSpan.FromSeconds(12))
        {
            var waitMs = (int)Math.Ceiling((TimeSpan.FromSeconds(12) - sinceResume).TotalMilliseconds);
            DesktopLog.Info($"ResetOverlays deferred reason={reason} waitMs={waitMs} (post-resume settle)");
            ScheduleOverlayReset(reason, delayMs: Math.Max(500, waitMs));
            return;
        }

        if (_currentScreenLayout?.HasFullScreenOnPrimary == true)
        {
            DesktopLog.Info($"ResetOverlays deferred reason={reason} (fullscreen detected)");
            ScheduleOverlayReset(reason, delayMs: 3000);
            return;
        }

        ResetOverlays(reason);
    }

    /// <summary>销毁全部 Overlay 并按当前会话重新实例化，再索取一帧状态。</summary>
    private void ResetOverlays(string reason)
    {
        DesktopLog.Info($"ResetOverlays reason={reason} count={_overlays.Count}");
        try
        {
            foreach (var overlay in _overlays.Values.ToList())
            {
                try { overlay.ForceClose(); }
                catch (Exception ex) { DesktopLog.Warn($"ResetOverlays ForceClose: {ex.Message}"); }
            }
            _overlays.Clear();

            if (_session.OverlayAllFour || _lastCelebrate)
                EnsureOverlays(forceAllFour: true);
            else if (_session.OverlayAdvancedPosition)
            {
                var positions = _lastStateSegments
                    .Select(s => _session.ResolveSegmentPosition(s.Position))
                    .Distinct();
                EnsureOverlays(positions);
            }
            else
                EnsureOverlays();

            foreach (var overlay in _overlays.Values)
                overlay.ScreenLayout = _currentScreenLayout;

            RequestOverlayStateRefresh();
        }
        catch (Exception ex)
        {
            // ExecutionEngineException 通常不可恢复，但仍尽量记日志并保留空 overlays，避免连环 Ensure。
            DesktopLog.Error($"ResetOverlays failed reason={reason}", ex);
            _overlays.Clear();
        }
    }

    private static Icon CreateTrayIconSafe()
    {
        try { return AppIconHelper.CreateTrayIcon(); }
        catch { return SystemIcons.Application; }
    }

    private void ShowConfig()
    {
        DesktopLog.Info("ShowConfig: menu click, scheduling deferred open");
        // 注意：打开/激活设置窗口不再停止到期呼吸——保持主条与三个氛围条持续同步呼吸，
        // 直到任务本身不再到期（重新设定时间 / 删除 / 完成确认）。
        // 托盘菜单为模态循环；延迟到菜单关闭后再创建/显示 FluentWindow，避免与 Win10 背景渲染争用 UI 线程。
        var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            DesktopLog.Info("ShowConfig: timer fired, invoking ShowConfigCore");
            Dispatcher.InvokeAsync(ShowConfigCore, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        timer.Start();
    }

    private void ShowConfigCore()
    {
        DesktopLog.Info($"ShowConfigCore: enter configNull={_config == null} " +
                        $"visible={_config?.IsVisible} loaded={_config?.IsLoaded}");
        try
        {
            if (_config == null)
            {
                DesktopLog.Info("ShowConfigCore: creating ConfigWindow");
                _config = new ConfigWindow(_ipc, _session, _updates);
                _config.HydrateFromSession(force: true);
                _config.ResetTaskEditorForNew();
            }

            if (_config == null)
            {
                DesktopLog.Error("ShowConfigCore: config unavailable");
                return;
            }

            if (!_config.IsVisible)
            {
                DesktopLog.Info("ShowConfigCore: calling Show()");
                _config.Show();
                DesktopLog.Info("ShowConfigCore: Show() returned");
            }
            else
            {
                DesktopLog.Info("ShowConfigCore: window already visible, skip Show()");
            }

            // Show 返回后再激活/拉数据，避免与 FluentWindow 首帧布局争用 UI 线程。
            Dispatcher.BeginInvoke(() =>
            {
                if (_config == null) return;
                _config.WindowState = WindowState.Normal;
                _config.Activate();
                _config.EnsureFluentBackdrop();
                _config.HydrateFromSession(force: true);
                _config.RequestRefresh();
                _config.FitHeightToTaskEditor(force: true);
                DesktopLog.Info($"ShowConfigCore: done IsVisible={_config.IsVisible} IsActive={_config.IsActive}");
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            DesktopLog.Error("ShowConfigCore failed", ex);
            _config = null;
            System.Windows.MessageBox.Show(
                $"无法打开设置窗口：{ex.Message}\n\n详情见 %APPDATA%\\Hope\\logs\\hope-desktop.log",
                "Hope", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void QuitAll()
    {
        _ipc.Send(new Command { Action = "quit" }); // 通知 Headless 正常退出
        _supervisor.StopWatching();

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _displayChangeDebounce?.Stop();
        _overlayResetDebounce?.Stop();
        _postResumeSettleTimer?.Stop();
        _ipcHealthCheckTimer?.Stop();
        _shellWatcher?.Dispose();
        _shellWatcher = null;
        StopSessionSnapshotRetry();

        _layoutTimer?.Stop();
        _updateTimer?.Stop();

        // 须先隐藏托盘再释放 Icon；否则 set_Visible 会访问已 Dispose 的 Icon.Handle。
        var tray = _tray;
        _tray = null;
        if (tray != null)
        {
            tray.Visible = false;
            var trayIcon = tray.Icon;
            tray.Icon = null;
            trayIcon?.Dispose();
            tray.Dispose();
        }
        foreach (var overlay in _overlays.Values) overlay.ForceClose();
        _overlays.Clear();
        _ipc.Dispose();
        _supervisor.Dispose();
        _instanceMutex?.ReleaseMutex();

        Shutdown();
    }
}
