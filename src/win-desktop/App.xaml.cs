using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
    private readonly Dictionary<string, OverlayWindow> _overlays = new();
    private string _currentBarPosition = OverlayWindow.PositionTop;
    private string _currentBarDirection = "forward";
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

        EnsureOverlays(_currentBarPosition, _currentBarDirection);

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

    private bool _startupConfigHandled;

    private void OnSettingsReceived(SettingsDto s)
    {
        DesktopLog.Info($"App.OnSettingsReceived barHeightPx={s.BarHeightPx} showConfigAtRuntime={s.ShowConfigAtRuntime} barPosition={s.BarPosition}");
        Dispatcher.BeginInvoke(() =>
        {
            _barHeightPx = Math.Clamp(s.BarHeightPx, 1, 10);
            _currentBarPosition = string.IsNullOrWhiteSpace(s.BarPosition) ? OverlayWindow.PositionTop : s.BarPosition;
            _currentBarDirection = s.BarDirection ?? "";
            EnsureOverlays(_currentBarPosition, _currentBarDirection);
            if (!_startupConfigHandled)
            {
                _startupConfigHandled = true;
                if (s.ShowConfigAtRuntime) ShowConfig();
            }
        });
    }

    private void OnStateReceived(StateMessage msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var all = msg.Segments ?? new List<Segment>();
            bool celebrate = IsCelebrateActive(all);
            string effectivePosition = celebrate ? "allFour" : _currentBarPosition;
            string effectiveDirection = celebrate ? "clockwise" : _currentBarDirection;
            EnsureOverlays(effectivePosition, effectiveDirection);
            DispatchState(msg);
            if (msg.Expired != null)
                foreach (var ev in msg.Expired) HandleExpired(ev);
        });
    }

    private static bool IsCelebrateActive(List<Segment> segments) =>
        segments.Any(s => s.Expired && s.Behaviors != null && s.Behaviors.Contains("celebrate"));

    /// <summary>根据当前全局位置/方向确保创建或销毁对应 OverlayWindow；庆祝模式临时需要四边窗口。</summary>
    private void EnsureOverlays(string position, string direction)
    {
        var wanted = new HashSet<string>();
        if (position == "allFour")
        {
            wanted.Add(OverlayWindow.PositionTop);
            wanted.Add(OverlayWindow.PositionRight);
            wanted.Add(OverlayWindow.PositionBottom);
            wanted.Add(OverlayWindow.PositionLeft);
        }
        else
        {
            wanted.Add(position);
        }

        bool isAllFour = wanted.Count == 4;
        string resolvedDirection = string.IsNullOrWhiteSpace(direction)
            ? (isAllFour ? "clockwise" : "forward")
            : direction;

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
            if (!_overlays.ContainsKey(pos))
            {
                _overlays[pos] = new OverlayWindow
                {
                    Position = pos,
                    Direction = LocalDirectionFor(pos, resolvedDirection),
                };
            }
        }
    }

    private static string LocalDirectionFor(string position, string globalDirection) => globalDirection switch
    {
        "clockwise" => position switch
        {
            "top" => "forward",
            "right" => "forward",
            "bottom" => "reverse",
            "left" => "reverse",
            _ => "forward",
        },
        "counterClockwise" => position switch
        {
            "top" => "reverse",
            "left" => "forward",
            "bottom" => "forward",
            "right" => "reverse",
            _ => "forward",
        },
        _ => globalDirection,
    };

    private void DispatchState(StateMessage msg)
    {
        var all = ExpandForCelebrate(msg.Segments ?? new List<Segment>());
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

    private static List<Segment> ExpandForCelebrate(List<Segment> segments)
    {
        if (!IsCelebrateActive(segments)) return segments;
        var expanded = new List<Segment>(segments.Count * 4);
        foreach (var s in segments)
        {
            foreach (var side in new[] { OverlayWindow.PositionTop, OverlayWindow.PositionRight, OverlayWindow.PositionBottom, OverlayWindow.PositionLeft })
            {
                expanded.Add(new Segment
                {
                    TaskId = s.TaskId,
                    Name = s.Name,
                    Color = s.Color,
                    Gif = s.Gif,
                    ImageMaxSize = s.ImageMaxSize,
                    BarStart = s.BarStart,
                    BarEnd = s.BarEnd,
                    Percent = s.Percent,
                    FillEnd = s.FillEnd,
                    EndAt = s.EndAt,
                    Expired = s.Expired,
                    Behaviors = s.Behaviors,
                    Position = side,
                });
            }
        }
        return expanded;
    }

    private void HandleExpired(ExpiredEvent ev)
    {
        // 一次性提醒：notify 弹气球。keep/hide/blink 为持续表现，由 Overlay 依据 Segment 状态驱动。
        if (ev.Behaviors != null && ev.Behaviors.Contains("notify"))
            _tray?.ShowBalloonTip(5000, "Hope · 任务到期", $"「{ev.Name}」已到达截止时间", ToolTipIcon.Info);
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
        // 打开设置即视为用户已查看到期提醒：停止当前闪烁脉冲。
        foreach (var overlay in _overlays.Values) overlay.AcknowledgeBlink();
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
        foreach (var overlay in _overlays.Values) overlay.ForceClose();
        _overlays.Clear();
        _ipc.Dispose();
        _supervisor.Dispose();
        _instanceMutex?.ReleaseMutex();

        Shutdown();
    }
}
