using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Hope.Desktop.Ipc;
using Hope.Desktop.Overlay;
using Hope.Desktop.Views;
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
    private NotifyIcon _tray = null!;
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

        // 应用 Windows 11 Fluent 主题，跟随系统亮 / 暗（WPF-UI，文档 §5.3.2）。
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();

        _supervisor = new HeadlessSupervisor();
        _supervisor.Start();

        _overlay = new OverlayWindow();

        _ipc = new IpcClient();
        _ipc.StateReceived += OnStateReceived;
        _ipc.Start();

        SetupTray();
    }

    private void OnStateReceived(StateMessage msg)
    {
        Dispatcher.Invoke(() =>
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
                _tray.ShowBalloonTip(5000, "Hope · 任务到期", $"「{ev.Name}」已到达截止时间", ToolTipIcon.Info);
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
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Hope · 盼头",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowConfig();
    }

    private void ShowConfig()
    {
        if (_config == null)
        {
            _config = new ConfigWindow(_ipc);
            _config.Closed += (_, _) => _config = null;
        }
        _config.Show();
        _config.WindowState = WindowState.Normal;
        _config.Activate();
    }

    private void ShowAbout()
    {
        System.Windows.MessageBox.Show(
            "Hope（盼头）· 桌面效率提示\n\n" +
            "屏幕顶端分段彩色进度条，点击穿透、不抢焦点。\n\n" +
            "可见范围：桌面 / 浏览器 / Office / 无边框或全屏优化的游戏。\n" +
            "真·独占全屏需安装全屏游戏拓展包（Phase 2）。",
            "关于 Hope", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void QuitAll()
    {
        _ipc.Send(new Command { Action = "quit" }); // 通知 Headless 正常退出
        _supervisor.StopWatching();

        _tray.Visible = false;
        _tray.Dispose();
        _overlay.ForceClose();
        _ipc.Dispose();
        _supervisor.Dispose();
        _instanceMutex?.ReleaseMutex();

        Shutdown();
    }
}
