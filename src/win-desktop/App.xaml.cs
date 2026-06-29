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
    private string _currentBarPosition = OverlayWindow.PositionTop;
    private string _currentBarDirection = "forward";
    private bool _allFour;    private HeadlessSupervisor _supervisor = null!;
    private NotifyIcon? _tray;
    private ConfigWindow? _config;
    /// <summary>已弹过到期通知的 taskId 集合；任务不再 expired 时清除，允许下个周期再次通知。</summary>
    private readonly HashSet<string> _notifiedTaskIds = new();

    private System.Threading.Mutex? _instanceMutex;
    private int _barHeightPx = 4;

    private UpdateCoordinator _updates = null!;
    private DispatcherTimer? _updateTimer;

    private ScreenLayoutInfo? _currentScreenLayout;
    private DispatcherTimer? _layoutTimer;

    /// <summary>启动后是否已检查过空任务列表；仅第一次为空时自动打开设置窗口。</summary>
    private bool _checkedEmptyTasks;

    /// <summary>是否已经因致命错误进入退出流程，避免重复弹窗。</summary>
    private bool _fatalExiting;

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
        ApplicationThemeManager.Changed += OnAppThemeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _supervisor = new HeadlessSupervisor();
        _supervisor.FatalFailure += OnHeadlessFatalFailure;

        EnsureOverlays();

        _ipc = new IpcClient();
        _ipc.StateReceived += OnStateReceived;
        _ipc.SettingsReceived += OnSettingsReceived;
        _ipc.VersionReceived += OnVersionReceived;
        _ipc.ConnectionChanged += OnConnectionChanged;
        _ipc.TasksReceived += OnTasksReceivedForEmptyCheck;
        _ipc.FatalDisconnected += OnIpcFatalDisconnected;

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

        SetupTray();
        SetupUpdates();
    }

    // 初始化自动更新：启动后延迟首检，之后每天检查一次；是否自动下载由全局设置控制。
    private void SetupUpdates()
    {
        _updates = new UpdateCoordinator(() => Dispatcher.Invoke(QuitAll));
        _updates.NewVersionAnnounced += OnNewVersionAnnounced;

        var initial = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        initial.Tick += (_, _) =>
        {
            initial.Stop();
            _ = _updates.CheckAsync(manual: false);
        };
        initial.Start();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromDays(1) };
        _updateTimer.Tick += (_, _) => _ = _updates.CheckAsync(manual: false);
        _updateTimer.Start();
    }

    private void OnNewVersionAnnounced(string tag)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_tray == null) return;
            string msg = _updates.Status == UpdateStatus.Ready
                ? $"新版本 {tag} 已下载，打开「设置 · 关于」即可安装"
                : $"发现新版本 {tag}，可在「设置 · 关于」中更新";
            _tray.ShowBalloonTip(5000, "Hope · 有可用更新", msg, ToolTipIcon.Info);
        });
    }

    // 连接建立后拉取一次全局设置，使进度条高度等即时生效，并上报当前主屏幕工作区尺寸。
    private void OnConnectionChanged(bool connected)
    {
        DesktopLog.Info($"IPC connection changed connected={connected}");
        if (connected)
        {
            _ipc.Send(new Command { Action = "getSettings" });
            _ipc.Send(new Command { Action = "getVersion" });
            _ipc.Send(new Command { Action = "listTasks" });
            SendScreenSize();
        }
    }

    private void OnTasksReceivedForEmptyCheck(List<TaskDto> tasks)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_checkedEmptyTasks) return;
            _checkedEmptyTasks = true;
            if (tasks.Count == 0)
            {
                DesktopLog.Info("No tasks found at startup, opening config window automatically");
                ShowConfig();
            }
        });
    }

    private void OnHeadlessFatalFailure(string reason)
    {
        Dispatcher.BeginInvoke(() => FatalExit("Hope · 后端启动失败", reason));
    }

    private void OnIpcFatalDisconnected(string reason)
    {
        Dispatcher.BeginInvoke(() => FatalExit("Hope · 通讯异常", reason));
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
        var rect = _currentScreenLayout?.EffectiveArea(_currentBarPosition) ?? SystemParameters.WorkArea;
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

    private bool _startupConfigHandled;

    /// <summary>检测主屏布局变化；变化时同步后端尺寸并让各 Overlay 立即重算位置。</summary>
    private void RefreshScreenLayout()
    {
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

        foreach (var overlay in _overlays.Values)
        {
            overlay.ScreenLayout = layout;
            overlay.RefreshLayout();
        }

        if (_ipc.IsConnected) SendScreenSize();
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
        Dispatcher.BeginInvoke(() => RefreshScreenLayout());
    }

    private void OnSettingsReceived(SettingsDto s)
    {
        DesktopLog.Info($"App.OnSettingsReceived barHeightPx={s.BarHeightPx} showConfigAtRuntime={s.ShowConfigAtRuntime} barPosition={s.BarPosition} barDirection={s.BarDirection}");
        Dispatcher.BeginInvoke(() =>
        {
            _barHeightPx = Math.Clamp(s.BarHeightPx, 1, 10);
            _currentBarPosition = string.IsNullOrWhiteSpace(s.BarPosition) ? OverlayWindow.PositionTop : s.BarPosition;
            _currentBarDirection = s.BarDirection ?? "";
            _allFour = s.AllFour;
            if (_updates != null) _updates.AutoUpdateEnabled = s.AutoUpdate;
            EnsureOverlays();
            if (!_startupConfigHandled)
            {
                _startupConfigHandled = true;
                if (s.ShowConfigAtRuntime) ShowConfig();
            }
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

    // 根据当前渲染的任务段刷新托盘 tooltip：任务按进度升序、每行「名称 倒计时」。
    // 受 NotifyIcon.Text 长度限制（≤127），超出时截断并以省略行提示。
    private void UpdateTrayTooltip(List<Segment> segments)
    {
        if (_tray == null) return;

        var now = DateTimeOffset.Now;
        var tasks = segments
            .Where(s => !string.IsNullOrEmpty(s.TaskId))
            .GroupBy(s => s.TaskId)
            .Select(g => g.First())
            .OrderBy(s => s.Percent)
            .ToList();

        const int maxLen = 127;
        const string nl = "\r\n";
        var text = new StringBuilder(TrayHeader());
        if (tasks.Count > 0)
        {
            // 名称按显示宽度（中文/全角=2，半角=1）补齐到统一宽度，使倒计时列对齐。
            int nameWidth = tasks.Max(s => DisplayWidth(s.Name ?? ""));
            text.Append(nl).Append("————");
            foreach (var s in tasks)
            {
                string line = $"{PadToWidth(s.Name ?? "", nameWidth)} {FormatTrayCountdown(s.EndAt, now)}";
                // 预留省略行（nl + '…' = 3 字符）的空间，超出则截断。
                if (text.Length + nl.Length + line.Length > maxLen - 3)
                {
                    text.Append(nl).Append('…');
                    break;
                }
                text.Append(nl).Append(line);
            }
        }

        string final = text.ToString();
        if (final.Length > maxLen) final = final.Substring(0, maxLen);
        _tray.Text = final;
    }

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

    private static string FormatTrayCountdown(DateTimeOffset endAt, DateTimeOffset now)
    {
        var remaining = endAt - now;
        if (remaining <= TimeSpan.Zero) return "已到期";
        return remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}天 {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void OnStateReceived(StateMessage msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var all = msg.Segments ?? new List<Segment>();
            bool celebrate = IsCelebrateActive(all);
            bool wantAllFour = _allFour || celebrate;
            EnsureOverlays(wantAllFour);
            DispatchState(msg, celebrate);
            UpdateTrayTooltip(all);

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
    private void EnsureOverlays(bool? forceAllFour = null)
    {
        bool allFour = forceAllFour ?? _allFour;
        var wanted = new HashSet<string>();
        if (allFour)
        {
            wanted.Add(OverlayWindow.PositionTop);
            wanted.Add(OverlayWindow.PositionRight);
            wanted.Add(OverlayWindow.PositionBottom);
            wanted.Add(OverlayWindow.PositionLeft);
        }
        else
        {
            wanted.Add(_currentBarPosition);
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
            var dir = LocalDirectionFor(pos);
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

    private string LocalDirectionFor(string position)
    {
        if (_allFour) return "forward"; // 四边环绕时方向已编码到 Segment 中
        return _currentBarDirection;
    }

    private void DispatchState(StateMessage msg, bool celebrate)
    {
        var all = celebrate && !_allFour ? ExpandForCelebrate(msg.Segments ?? new List<Segment>()) : (msg.Segments ?? new List<Segment>());
        var groups = all.GroupBy(s => string.IsNullOrWhiteSpace(s.Position) ? _currentBarPosition : s.Position)
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
            overlay.UpdateState(windowMsg, _barHeightPx);
        }
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

            string home = string.IsNullOrWhiteSpace(s.Position) ? _currentBarPosition : s.Position;
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
        menu.Items.Add("打开设置", null, (_, _) => ShowConfig());
        menu.Items.Add("检查更新", null, (_, _) => _ = _updates.CheckAsync(manual: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => QuitAll());

        _tray = new NotifyIcon
        {
            Icon = CreateTrayIconSafe(),
            Visible = true,
            Text = TrayHeader(),
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowConfig();
    }

    private void OnAppThemeChanged(ApplicationTheme currentTheme, System.Windows.Media.Color systemAccent) =>
        UpdateTrayIcon();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(() => UpdateTrayIcon());
    }

    private void UpdateTrayIcon()
    {
        if (_tray == null) return;
        try
        {
            var old = _tray.Icon;
            _tray.Icon = CreateTrayIconSafe();
            old?.Dispose();
            DesktopLog.Info($"Tray icon updated dark={AppIconHelper.IsDarkTheme()}");
        }
        catch (Exception ex)
        {
            DesktopLog.Warn($"Tray icon update failed: {ex.Message}");
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
                _config = new ConfigWindow(_ipc, _updates);
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
                _config.RequestRefresh();
                _config.FitHeightToTaskEditor();
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

        ApplicationThemeManager.Changed -= OnAppThemeChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

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
