using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Hope.Desktop.Ipc;
using Hope.Desktop.Overlay;
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
    private OverlayWindow _overlay = null!;
    private HeadlessSupervisor _supervisor = null!;
    private NotifyIcon? _tray;
    private ConfigWindow? _config;

    private System.Threading.Mutex? _instanceMutex;
    private bool _paused;
    private bool _hidden;
    private int _barHeightPx = 4;

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
        _supervisor.Start();

        _overlay = new OverlayWindow();

        _ipc = new IpcClient();
        _ipc.StateReceived += OnStateReceived;
        _ipc.SettingsReceived += OnSettingsReceived;
        _ipc.ConnectionChanged += OnConnectionChanged;
        _ipc.Start();

        SetupTray();
    }

    // 连接建立后拉取一次全局设置，使进度条高度等即时生效。
    private void OnConnectionChanged(bool connected)
    {
        DesktopLog.Info($"IPC connection changed connected={connected}");
        if (connected) _ipc.Send(new Command { Action = "getSettings" });
    }

    private void OnSettingsReceived(SettingsDto s)
    {
        DesktopLog.Info($"App.OnSettingsReceived barHeightPx={s.BarHeightPx}");
        Dispatcher.BeginInvoke(() => _barHeightPx = Math.Clamp(s.BarHeightPx, 1, 10));
    }

    private void OnStateReceived(StateMessage msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _overlay.UpdateState(msg, _barHeightPx);
            if (msg.Expired != null)
                foreach (var ev in msg.Expired) HandleExpired(ev);
        });
    }

    private void HandleExpired(ExpiredEvent ev)
    {
        switch (ev.Behavior)
        {
            case "notify":
                _tray?.ShowBalloonTip(5000, "Hope · 任务到期", $"「{ev.Name}」已到达截止时间", ToolTipIcon.Info);
                break;
            case "blink":
                _overlay.Blink();
                break;
            // keep / hide：无需额外动作（hide 时该任务已从活跃段中移除）
        }
    }

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => ShowConfig());
        var pauseItem = new ToolStripMenuItem("暂停");
        pauseItem.Click += (_, _) =>
        {
            _paused = !_paused;
            pauseItem.Text = _paused ? "继续" : "暂停";
            _ipc.Send(new Command { Action = _paused ? "pause" : "resume" });
        };
        menu.Items.Add(pauseItem);

        var hideItem = new ToolStripMenuItem("隐藏进度条");
        hideItem.Click += (_, _) =>
        {
            _hidden = !_hidden;
            hideItem.Text = _hidden ? "显示进度条" : "隐藏进度条";
            _ipc.Send(new Command { Action = _hidden ? "hide" : "show" });
        };
        menu.Items.Add(hideItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关于", null, (_, _) => ShowAbout());
        menu.Items.Add("退出", null, (_, _) => QuitAll());

        _tray = new NotifyIcon
        {
            Icon = CreateTrayIconSafe(),
            Visible = true,
            Text = "Hope · 盼头",
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
                _config = new ConfigWindow(_ipc);
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

            _config.WindowState = WindowState.Normal;
            _config.Activate();
            _config.RequestRefresh();
            DesktopLog.Info($"ShowConfigCore: done IsVisible={_config.IsVisible} IsActive={_config.IsActive}");
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

    private void ShowAbout()
    {
        Dispatcher.BeginInvoke(() =>
            System.Windows.MessageBox.Show(
                "Hope（盼头）· 桌面效率提示\n\n" +
                "屏幕顶端分段彩色进度条，点击穿透、不抢焦点。\n\n" +
                "可见范围：桌面 / 浏览器 / Office / 无边框或全屏优化的游戏。\n" +
                "真·独占全屏需安装全屏游戏拓展包（Phase 2）。",
                "关于 Hope", MessageBoxButton.OK, MessageBoxImage.Information));
    }

    private void QuitAll()
    {
        _ipc.Send(new Command { Action = "quit" }); // 通知 Headless 正常退出
        _supervisor.StopWatching();

        ApplicationThemeManager.Changed -= OnAppThemeChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

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
        _overlay.ForceClose();
        _ipc.Dispose();
        _supervisor.Dispose();
        _instanceMutex?.ReleaseMutex();

        Shutdown();
    }
}
