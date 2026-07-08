using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hope.Desktop;
using Hope.Desktop.Ipc;
using Hope.Desktop.Overlay;
using Hope.Desktop.State;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ColorConverter = System.Windows.Media.ColorConverter;
using WpfBackdrop = Wpf.Ui.Controls.WindowBackdropType;

namespace Hope.Desktop.Views;

/// <summary>任务配置窗口：多任务 CRUD，通过 IPC 同步至 Headless（文档 §5.3）。</summary>
public partial class ConfigWindow : Wpf.Ui.Controls.FluentWindow
{
    private const double MinFitWindowHeight = 400;
    private const double EditPanelMeasureWidth = 358;
    private const double FluentTitleBarFallbackHeight = 48;

    private readonly IpcClient _ipc;
    private readonly SessionState _session;
    private readonly Services.UpdateCoordinator? _updates;
    private readonly ObservableCollection<TaskRow> _rows = new();
    private ICollectionView? _rowsView;
    // 任务列表过滤模式：all=全部 / active=进行中 / completed=已完成（与 FilterBox 默认「进行中」一致）。
    private string _filterMode = "active";
    private string? _editingId;
    // 当前编辑任务图片路径（取代原图片地址输入框，作为数据来源）；空串表示无图片。
    private string _gifPath = "";
    // 图片预览精灵（复用 Overlay 的 ImageSprite，支持动图逐帧播放）。
    private Overlay.ImageSprite? _gifPreviewSprite;
    private DispatcherTimer? _gifPreviewTimer;
    // 当前编辑任务的到期提醒覆盖（表单不再暴露，保存时原样保留）；null = 继承全局。
    private List<string>? _editingBehaviors;
    private SettingsDto _settings = new();
    private bool _advancedSettingsVisible;
    // listTasks 刷新后恢复选中行时抑制 OnSelectTask，避免用服务端快照覆盖正在编辑的表单（含图片路径）。
    private bool _suppressTaskSelectionReload;
    private int _appliedSettingsRevision = -1;
    private int _appliedTasksRevision = -1;
    private TaskDto? _lastSavedDto;
    private string? _buildDtoError;
    private DateTimeOffset _taskCreatedAt = DateTimeOffset.Now;
    private bool _layoutReady;
    private readonly DispatcherTimer _autoSaveTimer;
    private DispatcherTimer? _nowClockTimer;

    public ConfigWindow(IpcClient ipc, SessionState session) : this(ipc, session, null) { }

    public ConfigWindow(IpcClient ipc, SessionState session, Services.UpdateCoordinator? updates)
    {
        DesktopLog.Info("ConfigWindow ctor: before InitializeComponent");
        // 必须在 InitializeComponent 之前初始化，因为 XAML 事件在加载期间就可能触发 TryAutoSaveTask
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autoSaveTimer.Tick += (_, _) => AutoSaveTask();
        InitializeComponent();
        DesktopLog.Info("ConfigWindow ctor: after InitializeComponent");

        AppIconHelper.ApplyWindowIcon(this);
        AppIconHelper.ApplyTitleBarIcon(AppTitleBar);
        StartNowClock();

        _ipc = ipc;
        _session = session;
        _updates = updates;
        if (_updates != null) _updates.StateChanged += OnUpdateStateChanged;
        _rowsView = System.Windows.Data.CollectionViewSource.GetDefaultView(_rows);
        _rowsView.Filter = RowMatchesFilter;
        TaskGrid.ItemsSource = _rowsView;
        _session.SettingsChanged += OnSessionSettingsChanged;
        _session.TasksChanged += OnSessionTasksChanged;
        _ipc.VersionReceived += OnBackendVersionReceived;

        PopulateTimeBoxes(StartHourBox, StartMinuteBox);
        PopulateTimeBoxes(EndHourBox, EndMinuteBox);
        SetupEditableTimeCombo(StartHourBox, 0, 23);
        SetupEditableTimeCombo(StartMinuteBox, 0, 59);
        SetupEditableTimeCombo(EndHourBox, 0, 23);
        SetupEditableTimeCombo(EndMinuteBox, 0, 59);

        RefreshBox.ValueChanged += OnSliderValueChanged;
        BarHeightBox.ValueChanged += OnSliderValueChanged;
        ImageMaxHeightBox.ValueChanged += OnSliderValueChanged;
        GlobalReminderNoneRadio.Checked += OnSettingsControlChanged;
        GlobalBlinkRadio.Checked += OnSettingsControlChanged;
        GlobalCelebrateRadio.Checked += OnSettingsControlChanged;
        GlobalNotifyCheck.Checked += OnSettingsControlChanged;
        GlobalNotifyCheck.Unchecked += OnSettingsControlChanged;
        AutostartCheck.Checked += OnSettingsControlChanged;
        AutostartCheck.Unchecked += OnSettingsControlChanged;
        ShowConfigAtRuntimeCheck.Checked += OnSettingsControlChanged;
        ShowConfigAtRuntimeCheck.Unchecked += OnSettingsControlChanged;
        AutoUpdateCheck.Checked += OnSettingsControlChanged;
        AutoUpdateCheck.Unchecked += OnSettingsControlChanged;
        AllowTelemetryCheck.Checked += OnSettingsControlChanged;
        AllowTelemetryCheck.Unchecked += OnSettingsControlChanged;
        BarPositionBox.SelectionChanged += OnSettingsSelectionChanged;
        BarForwardRadio.Checked += OnSettingsControlChanged;
        BarReverseRadio.Checked += OnSettingsControlChanged;
        TopForwardRadio.Checked += OnSettingsControlChanged;
        TopReverseRadio.Checked += OnSettingsControlChanged;
        BottomForwardRadio.Checked += OnSettingsControlChanged;
        BottomReverseRadio.Checked += OnSettingsControlChanged;
        LeftForwardRadio.Checked += OnSettingsControlChanged;
        LeftReverseRadio.Checked += OnSettingsControlChanged;
        RightForwardRadio.Checked += OnSettingsControlChanged;
        RightReverseRadio.Checked += OnSettingsControlChanged;
        AllFourCheck.Checked += OnSettingsControlChanged;
        AllFourCheck.Unchecked += OnSettingsControlChanged;
        AdvancedPositionCheck.Checked += OnSettingsControlChanged;
        AdvancedPositionCheck.Unchecked += OnSettingsControlChanged;
        AdvancedPositionCheck.Checked += OnAdvancedPositionChanged;
        AdvancedPositionCheck.Unchecked += OnAdvancedPositionChanged;

        var initPos = (BarPositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "top";
        UpdateSingleDirectionLabels(initPos);
        UpdateDirectionPanelsVisibility();
        ApplyAdvancedSettingsVisibility(false);
        UpdateAdvancedToggleButtonText();

        // 隐藏到托盘时暂停预览动画，重新显示时按需恢复，避免后台空耗。
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _gifPreviewSprite != null) _gifPreviewTimer?.Start();
            else _gifPreviewTimer?.Stop();
        };

        StatusText.SizeChanged += (_, _) => ScheduleFitHeightToTaskEditor();
        Loaded += (_, _) =>
        {
            _layoutReady = true;
            ScheduleFitHeightToTaskEditor();
            if (_ipc.IsConnected) RequestRefresh();
        };

        AboutVersionText.Text = FormatAppVersion();
        RenderUpdateUi();

        ContentRendered += (_, _) => EnsureFluentBackdrop();
        HookTaskFieldEvents();
        OnNew(this, new RoutedEventArgs());
        DesktopLog.Info("ConfigWindow ctor: done");
    }

    /// <summary>应用 Mica/Acrylic 并跟随系统主题；首帧与再次 Show 时均需调用。</summary>
    public void EnsureFluentBackdrop()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var backdrop = ResolveBackdrop();
            WindowBackdropType = backdrop;
            Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(this);
            Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, backdrop, updateAccents: true);
            DesktopLog.Info($"ConfigWindow backdrop ensured={backdrop} IsVisible={IsVisible}");
        }, DispatcherPriority.ApplicationIdle);
    }

    private static WpfBackdrop ResolveBackdrop()
    {
        if (Wpf.Ui.Controls.WindowBackdrop.IsSupported(WpfBackdrop.Mica))
            return WpfBackdrop.Mica;
        if (Wpf.Ui.Controls.WindowBackdrop.IsSupported(WpfBackdrop.Acrylic))
            return WpfBackdrop.Acrylic;
        return WpfBackdrop.None;
    }

    /// <summary>当前时间展示：当前时间 MM-dd HH:mm</summary>
    private static string FormatNowLabel()
    {
        var now = DateTime.Now;
        return $"当前时间 {now:MM-dd HH:mm}";
    }

    private void StartNowClock()
    {
        void Refresh()
        {
            NowTimeToolbarText.Text = FormatNowLabel();
            foreach (var row in _rows) row.RefreshProgressLabel();
        }

        Refresh();
        _nowClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _nowClockTimer.Tick += (_, _) => Refresh();
        _nowClockTimer.Start();
    }

    /// <summary>读取程序集版本信息并格式化为「应用程序版本号 vX.Y.Z」。</summary>
    private static string FormatAppVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();

        // 优先使用 InformationalVersion，它直接来自 csproj 的 <Version>。
        var infoAttr = Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute))
            as System.Reflection.AssemblyInformationalVersionAttribute;
        if (infoAttr != null && !string.IsNullOrWhiteSpace(infoAttr.InformationalVersion))
        {
            var v = infoAttr.InformationalVersion;
            // 去掉 commit hash（如 0.8.1+abcd1234 → 0.8.1）
            var plus = v.IndexOf('+');
            if (plus > 0) v = v[..plus];
            return $"应用程序版本号 v{v}";
        }

        var av = assembly.GetName().Version;
        return av != null ? $"应用程序版本号 v{av.Major}.{av.Minor}.{av.Build}" : "应用程序版本号 v0.0.0";
    }

    /// <summary>从 Headless 拉取任务列表与全局设置（在窗口已显示后调用）。</summary>
    public void RequestRefresh()
    {
        DesktopLog.Info("ConfigWindow RequestRefresh");
        _ipc.Send(new Command { Action = "listTasks" });
        _ipc.Send(new Command { Action = "getSettings" });
        _ipc.Send(new Command { Action = "getVersion" });
    }

    /// <summary>从 <see cref="SessionState"/> 同步当前快照到 UI（打开/再次显示设置窗时调用）。</summary>
    public void HydrateFromSession()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(HydrateFromSession);
            return;
        }
        if (_session.SettingsHydrated && _session.Settings != null)
            ApplySettingsFromServer(_session.Settings, _session.SettingsRevision);
        if (_session.TasksHydrated)
            ApplyTasksFromServer(_session.Tasks, _session.TasksRevision);
    }

    private void OnSessionSettingsChanged(SettingsDto s)
    {
        DesktopLog.Info($"ConfigWindow.OnSessionSettingsChanged barHeightPx={s.BarHeightPx} rev={_session.SettingsRevision}");
        if (_session.SettingsRevision <= _appliedSettingsRevision && _session.SettingsHydrated)
            return;
        if (Dispatcher.CheckAccess())
            ApplySettingsFromServer(s, _session.SettingsRevision);
        else
            Dispatcher.BeginInvoke(() => ApplySettingsFromServer(s, _session.SettingsRevision));
    }

    private void OnSessionTasksChanged(IReadOnlyList<TaskDto> tasks)
    {
        DesktopLog.Info($"ConfigWindow.OnSessionTasksChanged count={tasks.Count} rev={_session.TasksRevision}");
        if (_session.TasksRevision <= _appliedTasksRevision && _session.TasksHydrated)
            return;
        if (Dispatcher.CheckAccess())
            ApplyTasksFromServer(tasks, _session.TasksRevision);
        else
            Dispatcher.BeginInvoke(() => ApplyTasksFromServer(tasks, _session.TasksRevision));
    }

    private void ApplySettingsFromServer(SettingsDto s, int revision)
    {
        if (revision <= _appliedSettingsRevision && _session.SettingsHydrated)
        {
            DesktopLog.Info($"ConfigWindow.ApplySettingsFromServer skip stale rev={revision} applied={_appliedSettingsRevision}");
            return;
        }
        try
        {
            _settings = s;
            _session.Write.LoadingSettings = true;
            RefreshBox.Value = Math.Clamp(s.RefreshSec, 1, 10);
            BarHeightBox.Value = Math.Clamp(s.BarHeightPx, 1, 10);
            ImageMaxHeightBox.Value = Math.Clamp(s.ImageMaxHeightPx <= 0 ? 15 : s.ImageMaxHeightPx, 15, 30);
            RefreshValueText.Text = ((int)RefreshBox.Value).ToString();
            BarHeightValueText.Text = ((int)BarHeightBox.Value).ToString();
            ImageMaxHeightValueText.Text = ((int)ImageMaxHeightBox.Value).ToString();
            LoadGlobalBehaviors(s.ExpiredBehaviors);
            AutostartCheck.IsChecked = s.Autostart;
            ShowConfigAtRuntimeCheck.IsChecked = s.ShowConfigAtRuntime;
            AutoUpdateCheck.IsChecked = s.AutoUpdate;
            AllowTelemetryCheck.IsChecked = s.AllowTelemetry;
            SelectComboByTag(BarPositionBox, s.BarPosition);
            AdvancedPositionCheck.IsChecked = s.AdvancedPosition;
            UpdateSingleDirectionLabels(s.BarPosition);
            ApplyBarDirection(string.IsNullOrWhiteSpace(s.BarDirection) ? "forward" : s.BarDirection);
            ApplyBarDirections(s.BarDirections);
            AllFourCheck.IsChecked = s.AllFour;
            UpdateDirectionPanelsVisibility();
            ApplyAdvancedSettingsVisibility(s.ShowAdvancedSettings);
            _appliedSettingsRevision = revision;
            DesktopLog.Info("ConfigWindow.ApplySettingsFromServer applied to UI");
        }
        catch (Exception ex)
        {
            DesktopLog.Error("ConfigWindow.ApplySettingsFromServer failed", ex);
        }
        finally
        {
            _session.Write.LoadingSettings = false;
        }
    }

    // 后端（内核）版本来自 IPC 的 getVersion 响应；展示在「关于 · 更新与许可证」区。
    private void OnBackendVersionReceived(string version)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var v = string.IsNullOrWhiteSpace(version) ? "未知" : version.TrimStart('v', 'V');
            BackendVersionText.Text = $"内核版本号 v{v}";
        });
    }

    private void OnSettingsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_session.Write.CanCommitSettings(_session)) return;
        if (e.AddedItems.Count > 0) CommitSettings();
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender == RefreshBox) RefreshValueText.Text = ((int)RefreshBox.Value).ToString();
        if (sender == BarHeightBox) BarHeightValueText.Text = ((int)BarHeightBox.Value).ToString();
        if (sender == ImageMaxHeightBox) ImageMaxHeightValueText.Text = ((int)ImageMaxHeightBox.Value).ToString();
        if (_session.Write.CanCommitSettings(_session)) CommitSettings();
    }

    private void OnSettingsControlChanged(object sender, RoutedEventArgs e)
    {
        if (_session.Write.CanCommitSettings(_session)) CommitSettings();
    }

    private void CommitSettings()
    {
        if (!_session.Write.CanCommitSettings(_session)) return;
        // InitializeComponent() 期间会触发 BarPositionBox 的 SelectionChanged，
        // 此时 _ipc 尚未赋值，连控件也尚未完全构造，因此只能静默返回。
        if (_ipc == null) return;
        if (!_ipc.IsConnected)
        {
            SettingsStatus.Text = "未连接到核心进程（hope-headless），无法保存设置";
            return;
        }

        _settings.RefreshSec = (int)RefreshBox.Value;
        _settings.BarHeightPx = (int)BarHeightBox.Value;
        _settings.ImageMaxHeightPx = (int)ImageMaxHeightBox.Value;
        _settings.ExpiredBehaviors = CollectGlobalBehaviors();
        _settings.Autostart = AutostartCheck.IsChecked == true;
        _settings.ShowConfigAtRuntime = ShowConfigAtRuntimeCheck.IsChecked == true;
        _settings.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        _settings.AllowTelemetry = AllowTelemetryCheck.IsChecked == true;
        _settings.BarPosition = (BarPositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "top";
        _settings.BarDirection = CollectBarDirection();
        _settings.BarDirections = CollectBarDirections();
        _settings.AdvancedPosition = AdvancedPositionCheck.IsChecked == true;
        _settings.AllFour = AllFourCheck.IsChecked == true;
        _settings.ShowAdvancedSettings = _advancedSettingsVisible;
        var rect = SystemParameters.WorkArea;
        _settings.ScreenWidth = rect.Width;
        _settings.ScreenHeight = rect.Height;

        if (_updates != null) _updates.AutoUpdateEnabled = _settings.AutoUpdate;

        _ipc.Send(new Command { Action = "updateSettings", Settings = _settings });
        ApplyAutostart(_settings.Autostart);
        SettingsStatus.Text = "";
    }

    private static void SelectComboByTag(ComboBox box, string? tag)
    {
        if (box == null) return;
        foreach (ComboBoxItem? item in box.Items)
        {
            if (item == null) continue;
            if (item.Tag?.ToString() == tag) { item.IsSelected = true; return; }
        }
    }

    private void UpdateSingleDirectionLabels(string position)
    {
        if (BarForwardRadio == null || BarReverseRadio == null) return;
        bool vertical = position is "left" or "right";
        BarForwardRadio.Content = vertical ? "↓" : "→";
        BarReverseRadio.Content = vertical ? "↑" : "←";
        BarForwardRadio.ToolTip = vertical ? "从上到下" : "从左到右";
        BarReverseRadio.ToolTip = vertical ? "从下到上" : "从右到左";
    }

    private void ApplyBarDirection(string direction)
    {
        if (BarForwardRadio == null || BarReverseRadio == null) return;
        if (direction == "reverse")
            BarReverseRadio.IsChecked = true;
        else
            BarForwardRadio.IsChecked = true;
    }

    private string CollectBarDirection()
    {
        if (BarReverseRadio == null) return "forward";
        return BarReverseRadio.IsChecked == true ? "reverse" : "forward";
    }

    private static Dictionary<string, string> DefaultBarDirections() => new()
    {
        ["top"] = "forward",
        ["bottom"] = "forward",
        ["left"] = "forward",
        ["right"] = "forward",
    };

    private void ApplyBarDirections(Dictionary<string, string>? directions)
    {
        if (TopForwardRadio == null) return;
        var dirs = DefaultBarDirections();
        if (directions != null)
        {
            foreach (var (key, value) in directions)
            {
                if (value is "forward" or "reverse")
                    dirs[key] = value;
            }
        }
        SetEdgeDirectionRadios(dirs["top"], TopForwardRadio, TopReverseRadio);
        SetEdgeDirectionRadios(dirs["bottom"], BottomForwardRadio, BottomReverseRadio);
        SetEdgeDirectionRadios(dirs["left"], LeftForwardRadio, LeftReverseRadio);
        SetEdgeDirectionRadios(dirs["right"], RightForwardRadio, RightReverseRadio);
    }

    private static void SetEdgeDirectionRadios(string direction, System.Windows.Controls.RadioButton forward, System.Windows.Controls.RadioButton reverse)
    {
        if (forward == null || reverse == null) return;
        if (direction == "reverse")
            reverse.IsChecked = true;
        else
            forward.IsChecked = true;
    }

    private Dictionary<string, string> CollectBarDirections()
    {
        return new Dictionary<string, string>
        {
            ["top"] = TopReverseRadio?.IsChecked == true ? "reverse" : "forward",
            ["bottom"] = BottomReverseRadio?.IsChecked == true ? "reverse" : "forward",
            ["left"] = LeftReverseRadio?.IsChecked == true ? "reverse" : "forward",
            ["right"] = RightReverseRadio?.IsChecked == true ? "reverse" : "forward",
        };
    }

    private void UpdateDirectionPanelsVisibility()
    {
        if (PerEdgeDirectionPanel == null || AdvancedPositionCheck == null || AllFourCheck == null)
            return;
        bool advancedPosition = AdvancedPositionCheck.IsChecked == true;
        bool allFour = AllFourCheck.IsChecked == true;
        AdvancedPositionCheck.Visibility = allFour ? Visibility.Collapsed : Visibility.Visible;
        PerEdgeDirectionPanel.Visibility = advancedPosition && !allFour ? Visibility.Visible : Visibility.Collapsed;
        if (TaskPositionRow != null)
            TaskPositionRow.Visibility = advancedPosition && !allFour ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAllFourChanged(object sender, RoutedEventArgs e)
    {
        UpdateDirectionPanelsVisibility();
        ScheduleFitHeightToTaskEditor();
        if (_session.Write.CanCommitSettings(_session)) CommitSettings();
    }

    private void OnBarPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (BarForwardRadio == null) return; // InitializeComponent 期间 AdvancedOptionsPanel 尚未解析
        var pos = (BarPositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "top";
        UpdateSingleDirectionLabels(pos);
        if (_session.Write.CanCommitSettings(_session)) CommitSettings();
    }

    private void OnAdvancedPositionChanged(object sender, RoutedEventArgs e)
    {
        UpdateDirectionPanelsVisibility();
        ScheduleFitHeightToTaskEditor();
    }

    private void OnToggleAdvancedOptionsClick(object sender, RoutedEventArgs e)
    {
        ApplyAdvancedSettingsVisibility(!_advancedSettingsVisible);
        if (_session.Write.CanCommitSettings(_session)) CommitSettings();
        ScheduleFitHeightToTaskEditor();
    }

    private void ApplyAdvancedSettingsVisibility(bool visible)
    {
        _advancedSettingsVisible = visible;
        if (BarAdvancedOptionsPanel != null)
            BarAdvancedOptionsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateAdvancedToggleButtonText();
    }

    private void UpdateAdvancedToggleButtonText()
    {
        if (ToggleAdvancedOptionsButton != null)
            ToggleAdvancedOptionsButton.Content = _advancedSettingsVisible ? "隐藏高级选项" : "显示高级选项";
    }

    private void OnEasterEggClick(object sender, MouseButtonEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "恭喜你发现了彩蛋，点击确定打开浏览器访问，如果你打不开，可能需要一点小小的魔法。",
            "Hope · 彩蛋",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.OK) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.marxists.org/chinese/reference-books/poems-of-struggle/america-songs.htm",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DesktopLog.Info($"打开彩蛋链接失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 按右侧 Tab 内容实测高度调整窗体高度（§5.3.3 新增 7）。
    /// 取「任务编辑」与「全局设置」两个面板的较大高度，确保两个 Tab 都无需滚动条即可完整展示。
    /// </summary>
    public void FitHeightToTaskEditor()
    {
        if (!_layoutReady || TaskEditPanel == null) return;

        double editContentH = MeasurePanelHeight(TaskEditPanel);
        double settingsContentH = MeasurePanelHeight(SettingsPanel);
        double contentH = Math.Max(editContentH, settingsContentH);

        const double tabHeaderH = 28;
        double panelMarginV = TaskEditPanel.Margin.Top + TaskEditPanel.Margin.Bottom;
        double gridMarginV = RootGrid.Margin.Top + RootGrid.Margin.Bottom;
        double leftMinH = 32 + 8 + 32 + 80; // 列表头 + 删除按钮 + 最小表格区
        double titleBarH = AppTitleBar?.ActualHeight > 0
            ? AppTitleBar.ActualHeight
            : FluentTitleBarFallbackHeight;

        double clientH = Math.Max(contentH + tabHeaderH + panelMarginV, leftMinH) + gridMarginV + titleBarH;
        Height = Math.Max(MinFitWindowHeight, clientH);
    }

    // 在无限高度约束下测量面板内容的期望高度；面板未排布时退回到约定测量宽度。
    private static double MeasurePanelHeight(FrameworkElement? panel)
    {
        if (panel == null) return 0;
        panel.UpdateLayout();
        double w = panel.ActualWidth > 0 ? panel.ActualWidth : EditPanelMeasureWidth;
        panel.Measure(new System.Windows.Size(w, double.PositiveInfinity));
        return panel.DesiredSize.Height;
    }

    private void ScheduleFitHeightToTaskEditor()
    {
        if (!_layoutReady) return;
        Dispatcher.BeginInvoke(FitHeightToTaskEditor, DispatcherPriority.Loaded);
    }

    private static int ParseIntItem(ComboBox box, int fallback) =>
        box.SelectedItem is string s && int.TryParse(s, out var v) ? v : fallback;

    // ===== 到期提醒（默认自动显示，可叠加勾选：闪烁 / 全屏庆祝 / 系统通知）=====

    // 全局到期提醒集合（可空）：呼吸 / 全屏庆祝 互斥（radio），系统通知可独立叠加。
    private List<string> CollectGlobalBehaviors()
    {
        var list = new List<string>();
        if (GlobalBlinkRadio.IsChecked == true) list.Add("blink");
        if (GlobalCelebrateRadio.IsChecked == true) list.Add("celebrate");
        if (GlobalNotifyCheck.IsChecked == true) list.Add("notify");
        return list;
    }

    private void LoadGlobalBehaviors(List<string>? behaviors)
    {
        var set = behaviors ?? new List<string>();
        // 互斥：庆祝优先于呼吸；二者皆无则「仅自动显示」。兼容旧版可能同时含二者的数据。
        if (set.Contains("celebrate")) GlobalCelebrateRadio.IsChecked = true;
        else if (set.Contains("blink")) GlobalBlinkRadio.IsChecked = true;
        else GlobalReminderNoneRadio.IsChecked = true;
        GlobalNotifyCheck.IsChecked = set.Contains("notify");
    }

    // 任务级到期提醒：表单已不再暴露选项，统一沿用全局。
    // 但保留每任务覆盖能力——编辑时原样保留任务已有的 ExpiredBehaviors（新任务为 null=继承全局）。
    private List<string>? CollectTaskBehaviors() => _editingBehaviors;

    // ===== 循环（仅定时任务）=====

    private (CheckBox chk, int weekday)[] WeekdayMap() => new[]
    {
        (ChkMon, 1), (ChkTue, 2), (ChkWed, 3), (ChkThu, 4),
        (ChkFri, 5), (ChkSat, 6), (ChkSun, 0),
    };

    private IEnumerable<CheckBox> WeekdayChecks() => new[] { ChkMon, ChkTue, ChkWed, ChkThu, ChkFri, ChkSat, ChkSun };

    private string RecurMode() => (RecurModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private void OnRecurModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRecurVisibility();
        TryAutoSaveTask();
        ScheduleFitHeightToTaskEditor();
    }

    // 间隔输入框仅允许数字（范围在保存/预览处校验为 3~799）。
    private void OnRecurIntervalChanged(object sender, TextChangedEventArgs e)
    {
        var box = RecurIntervalBox;
        var digits = new string(box.Text.Where(char.IsDigit).ToArray());
        if (digits != box.Text)
        {
            int caret = box.CaretIndex;
            box.Text = digits;
            box.CaretIndex = Math.Min(caret, digits.Length);
            return; // 赋值会再次触发，落到下面分支
        }
        TryAutoSaveTask();
    }

    private void UpdateRecurVisibility()
    {
        var mode = RecurMode();
        if (RecurIntervalRow != null)
            RecurIntervalRow.Visibility = mode == "everyN" ? Visibility.Visible : Visibility.Collapsed;
        if (RecurWeekRow != null)
            RecurWeekRow.Visibility = mode == "weekly" ? Visibility.Visible : Visibility.Collapsed;
    }

    // 组装循环规则：不循环或非定时任务返回 null。
    private RecurrenceDto? CollectRecurrence()
    {
        if (SelectedType() != "scheduled") return null;
        var mode = RecurMode();
        switch (mode)
        {
            case "daily":
                return new RecurrenceDto { Mode = "daily" };
            case "everyN":
                int.TryParse(RecurIntervalBox.Text, out var iv);
                return new RecurrenceDto { Mode = "everyN", Interval = iv };
            case "weekly":
                var days = WeekdayMap().Where(p => p.chk.IsChecked == true).Select(p => p.weekday).ToList();
                return new RecurrenceDto { Mode = "weekly", Weekdays = days };
            default:
                return null;
        }
    }

    private void LoadRecurrence(RecurrenceDto? rec)
    {
        string mode = rec?.Mode ?? "";
        foreach (ComboBoxItem item in RecurModeBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
            { RecurModeBox.SelectedItem = item; break; }
        }
        int iv = rec?.Interval ?? 0;
        RecurIntervalBox.Text = (iv >= 2 && iv <= 800) ? iv.ToString() : "2";
        var set = rec?.Weekdays ?? new List<int>();
        foreach (var (chk, wd) in WeekdayMap()) chk.IsChecked = set.Contains(wd);
        UpdateRecurVisibility();
    }

    // 写入 / 删除 HKCU Run 项，实现开机自启（§5.3.3 新增 3）。
    private static void ApplyAutostart(bool enable)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true)
                            ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKey);
            if (key == null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue("Hope", $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue("Hope", throwOnMissingValue: false);
            }
        }
        catch { /* 注册表不可写时静默；设置仍持久化在 config.json */ }
    }

    // ============ 自动更新（关于页） ============

    private void OnUpdateStateChanged() => Dispatcher.BeginInvoke(RenderUpdateUi);

    // 根据协调器状态刷新「关于」页的更新区：状态文本、进度、按钮与更新说明的可见性。
    private void RenderUpdateUi()
    {
        if (_updates == null) return;
        var st = _updates.Status;

        UpdateStatusText.Text = string.IsNullOrEmpty(_updates.Message)
            ? "点击「检查更新」获取最新版本。"
            : _updates.Message;

        bool downloading = st == Services.UpdateStatus.Downloading;
        UpdateProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = Math.Clamp(_updates.DownloadProgress * 100.0, 0, 100);

        CheckUpdateButton.IsEnabled = st != Services.UpdateStatus.Checking && !downloading;

        bool hasNew = st is Services.UpdateStatus.Available or Services.UpdateStatus.Ready;
        DownloadUpdateButton.Visibility = st == Services.UpdateStatus.Available ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Visibility = st == Services.UpdateStatus.Ready ? Visibility.Visible : Visibility.Collapsed;
        SkipVersionButton.Visibility = hasNew ? Visibility.Visible : Visibility.Collapsed;

        var notes = _updates.Latest?.Notes ?? "";
        if (hasNew && !string.IsNullOrWhiteSpace(notes))
        {
            UpdateNotesBox.Text = notes;
            UpdateNotesBox.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateNotesBox.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCheckUpdate(object sender, RoutedEventArgs e) =>
        _ = _updates?.CheckAsync(manual: true);

    private void OnDownloadUpdate(object sender, RoutedEventArgs e) =>
        _ = _updates?.DownloadAsync();

    private void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_updates == null) return;
        var r = System.Windows.MessageBox.Show(
            "将关闭 Hope 并安装新版本，安装完成后会自动重新启动。是否继续？",
            "Hope · 安装更新", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.OK) _updates.InstallNow();
    }

    private void OnSkipVersion(object sender, RoutedEventArgs e) => _updates?.SkipCurrent();

    // 弹出第三方组件引用与许可证信息（只读、可滚动、可复制）。
    private void OnShowLicenses(object sender, RoutedEventArgs e)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = LicensesText,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.Black,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 12,
            Margin = new Thickness(16),
        };

        var win = new Window
        {
            Title = "Hope · 开源许可证",
            Width = 600,
            Height = 620,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White,
            Content = box,
        };
        win.ShowDialog();
    }

    // 本项目使用的第三方 SDK / 库 / 组件及其许可证（均为宽松型，与 MIT 兼容）。
    private const string LicensesText =
@"Hope（盼头）— 开源组件与许可证

本软件以 MIT 许可证发布。下列第三方 SDK / 库 / 组件按其各自许可证使用，特此致谢并保留其版权声明。

────────────────────────────────────────
桌面端（C# / .NET）
────────────────────────────────────────

• .NET 10 运行时、WPF、Windows Forms（System.Drawing 等）
  作者：.NET Foundation 与贡献者
  许可证：MIT License
  https://github.com/dotnet

• WPF-UI  v4.2.0
  作者：lepo.co（lepoco）
  许可证：MIT License
  https://github.com/lepoco/wpfui

────────────────────────────────────────
核心后端（Go）
────────────────────────────────────────

• Go 标准库与工具链
  作者：The Go Authors / Google
  许可证：BSD-3-Clause License
  https://go.dev/LICENSE

• github.com/Microsoft/go-winio  v0.6.2
  作者：Microsoft
  许可证：MIT License
  https://github.com/microsoft/go-winio

• golang.org/x/sys  v0.46.0
  作者：The Go Authors
  许可证：BSD-3-Clause License
  https://cs.opensource.google/go/x/sys

────────────────────────────────────────
构建 / 打包工具（不随程序分发其源码）
────────────────────────────────────────

• Inno Setup（生成安装包）
  作者：Jordan Russell / Martijn Laan
  许可证：Inno Setup License（类 BSD，允许为任意软件制作安装程序）
  https://jrsoftware.org/isinfo.php

════════════════════════════════════════
MIT License（全文）
════════════════════════════════════════

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

════════════════════════════════════════
BSD-3-Clause License（全文）
════════════════════════════════════════

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS ""AS IS""
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.";

    private void OnAddTask(object sender, RoutedEventArgs e)
    {
        OnNew(sender, e);
        MainTabs.SelectedItem = TaskTab;
        NameBox.Focus();
    }

    private void OnPresetName(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl b && b.Content is string name) NameBox.Text = name;
    }

    private void OnDateQuickFill(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string tag) return;
        var sep = tag.IndexOf(':');
        if (sep < 0) return;
        var picker = tag[..sep] == "StartDate" ? StartDatePicker : EndDatePicker;
        var today = DateTime.Today;
        var date = tag[(sep + 1)..] switch
        {
            "Today" => today,
            "Tomorrow" => today.AddDays(1),
            "DayAfter" => today.AddDays(2),
            "Week" => today.AddDays(7),
            "Month" => today.AddMonths(1),
            _ => today,
        };
        picker.SelectedDate = date;
        TryAutoSaveTask();
        ScheduleFitHeightToTaskEditor();
    }

    private void OnTimeQuickFill(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string tag) return;
        var sep = tag.IndexOf(':');
        if (sep < 0) return;
        var target = tag[..sep];
        var preset = tag[(sep + 1)..];

        DatePicker datePicker;
        ComboBox hourBox, minuteBox;
        if (target == "StartTime")
        {
            datePicker = StartDatePicker;
            hourBox = StartHourBox;
            minuteBox = StartMinuteBox;
        }
        else
        {
            datePicker = EndDatePicker;
            hourBox = EndHourBox;
            minuteBox = EndMinuteBox;
        }

        var when = preset switch
        {
            "Now" => DateTime.Now,
            "H0.5" => DateTime.Now.AddMinutes(30),
            "H1" => DateTime.Now.AddHours(1),
            "H2" => DateTime.Now.AddHours(2),
            "H8" => DateTime.Now.AddHours(8),
            "H12" => DateTime.Now.AddHours(12),
            _ => DateTime.Now,
        };

        SetDateTime(datePicker, hourBox, minuteBox, when);
        TryAutoSaveTask();
        ScheduleFitHeightToTaskEditor();
    }

    private static void PopulateTimeBoxes(ComboBox hour, ComboBox minute)
    {
        for (int h = 0; h < 24; h++) hour.Items.Add(h.ToString("D2"));
        for (int m = 0; m < 60; m++) minute.Items.Add(m.ToString("D2"));
    }

    private readonly Dictionary<ComboBox, DispatcherTimer> _timeNormalizeTimers = new();

    /// <summary>时/分下拉框：可手动输入或选择；失焦或防抖后把越界值钳制到合法区间。</summary>
    private void SetupEditableTimeCombo(ComboBox box, int min, int max)
    {
        var normalizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        normalizeTimer.Tick += (_, _) =>
        {
            normalizeTimer.Stop();
            NormalizeTimeCombo(box, min, max);
        };
        _timeNormalizeTimers[box] = normalizeTimer;

        void AttachEditableTextBox()
        {
            if (box.Template.FindName("PART_EditableTextBox", box) is not System.Windows.Controls.TextBox tb) return;
            if (ReferenceEquals(tb.Tag, box)) return;
            tb.Tag = box;
            tb.MaxLength = 2;
            tb.PreviewTextInput += (_, e) =>
            {
                e.Handled = e.Text.Length > 0 && !e.Text.All(char.IsDigit);
            };
            tb.LostFocus += (_, _) =>
            {
                normalizeTimer.Stop();
                NormalizeTimeCombo(box, min, max);
                TryAutoSaveTask();
            };
            tb.TextChanged += (_, _) =>
            {
                normalizeTimer.Stop();
                normalizeTimer.Start();
                TryAutoSaveTask();
            };
        }

        if (box.IsLoaded) AttachEditableTextBox();
        else box.Loaded += (_, _) => AttachEditableTextBox();

        box.SelectionChanged += (_, _) =>
        {
            normalizeTimer.Stop();
            NormalizeTimeCombo(box, min, max);
            TryAutoSaveTask();
        };
        box.LostFocus += (_, _) =>
        {
            normalizeTimer.Stop();
            NormalizeTimeCombo(box, min, max);
        };
    }

    private static void NormalizeTimeCombo(ComboBox box, int min, int max)
    {
        if (!TryReadTimeComboRaw(box, out var raw))
        {
            ApplyTimeComboValue(box, min);
            return;
        }
        if (!int.TryParse(raw, out var n))
        {
            ApplyTimeComboValue(box, min);
            return;
        }
        ApplyTimeComboValue(box, Math.Clamp(n, min, max));
    }

    private static void ApplyTimeComboValue(ComboBox box, int value)
    {
        var text = value.ToString("D2");
        box.Text = text;
        box.SelectedItem = text;
    }

    private static bool TryReadTimeComboRaw(ComboBox box, out string raw)
    {
        raw = string.IsNullOrWhiteSpace(box.Text)
            ? box.SelectedItem?.ToString()?.Trim() ?? ""
            : box.Text.Trim();
        return !string.IsNullOrWhiteSpace(raw);
    }

    private static bool TryParseTimeCombo(ComboBox box, int min, int max, out int value)
    {
        value = min;
        if (!TryReadTimeComboRaw(box, out var raw)) return false;
        if (!int.TryParse(raw, out var n)) return false;
        value = Math.Clamp(n, min, max);
        return true;
    }

    /// <summary>定时任务允许跨午夜（截止时分 ≤ 开始时分）；其余情况要求截止晚于开始。</summary>
    private static bool IsTaskTimeRangeValid(string type, DateTimeOffset? start, DateTimeOffset end,
        DateTimeOffset taskStart, out string error)
    {
        error = "";
        var rangeStart = type == "instant" ? taskStart : start ?? taskStart;
        if (end > rangeStart) return true;

        if (type == "scheduled" && start.HasValue)
        {
            var s = start.Value.LocalDateTime;
            var e = end.LocalDateTime;
            if (e.Date < s.Date)
            {
                error = "截止时间不能早于开始时间";
                return false;
            }
            if (e.Date <= s.Date.AddDays(1) && e.TimeOfDay <= s.TimeOfDay)
                return true;
        }

        error = type == "instant"
            ? "截止时间不能早于任务开始时刻"
            : "截止时间不能早于开始时间";
        return false;
    }

    private static void SetDateTime(DatePicker date, ComboBox hour, ComboBox minute, DateTime value)
    {
        date.SelectedDate = value.Date;
        var hs = value.Hour.ToString("D2");
        var ms = value.Minute.ToString("D2");
        hour.Text = hs;
        hour.SelectedItem = hs;
        minute.Text = ms;
        minute.SelectedItem = ms;
    }

    private static bool TryComposeDateTime(DatePicker date, ComboBox hour, ComboBox minute, out DateTimeOffset value)
    {
        value = default;
        if (date.SelectedDate is not DateTime d) return false;
        if (!TryParseTimeCombo(hour, 0, 23, out var h)) return false;
        if (!TryParseTimeCombo(minute, 0, 59, out var m)) return false;
        var dt = new DateTime(d.Year, d.Month, d.Day, h, m, 0);
        value = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        return true;
    }

    private void ApplyTasksFromServer(IReadOnlyList<TaskDto> tasks, int revision)
    {
        if (revision <= _appliedTasksRevision && _session.TasksHydrated)
        {
            DesktopLog.Info($"ConfigWindow.ApplyTasksFromServer skip stale rev={revision} applied={_appliedTasksRevision}");
            return;
        }
        try
        {
            var editingId = _editingId;
            _suppressTaskSelectionReload = true;

            var newRows = new List<TaskRow>(tasks.Count);
            foreach (var t in tasks)
                newRows.Add(TaskRow.From(t));

            _rows.Clear();
            foreach (var row in newRows)
                _rows.Add(row);
            _rowsView?.Refresh();

            // 仅恢复列表高亮，不重载表单，避免覆盖用户未保存/刚保存的字段（尤其是图片路径）。
            if (editingId != null)
            {
                var row = _rows.FirstOrDefault(r => r.Id == editingId);
                if (row != null) TaskGrid.SelectedItem = row;
            }

            if (_editingId != null)
            {
                var editing = _rows.FirstOrDefault(r => r.Id == _editingId);
                if (editing != null) UpdateTaskActionButtons(editing);
            }

            foreach (var id in _session.Write.PendingCompleteIdsSnapshot())
            {
                if (_rows.Any(r => r.Id == id && r.Completed))
                    _session.Write.RemovePendingComplete(id);
            }

            if (_session.Write.PendingCompleteCount == 0)
                _session.Write.LoadingTask = false;

            _appliedTasksRevision = revision;

            DesktopLog.Info($"ConfigWindow.ApplyTasksFromServer grid rows={_rows.Count} visible={_rowsView?.Cast<object>().Count() ?? 0}");
        }
        catch (Exception ex)
        {
            DesktopLog.Error("ConfigWindow.ApplyTasksFromServer failed", ex);
        }
        finally
        {
            _suppressTaskSelectionReload = false;
        }
    }

    // 任务列表过滤谓词：按 _filterMode 决定行是否显示。
    private bool RowMatchesFilter(object item)
    {
        if (item is not TaskRow row) return true;
        return _filterMode switch
        {
            "active" => !row.Completed,
            "completed" => row.Completed,
            _ => true,
        };
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || box.SelectedItem is not ComboBoxItem item) return;
        _filterMode = item.Tag?.ToString() ?? "all";
        _rowsView?.Refresh();
    }

    private void OnDeleteCompleted(object sender, RoutedEventArgs e)
    {
        var completed = _rows.Where(r => r.Completed).ToList();
        if (completed.Count == 0) { StatusText.Text = "没有已完成的任务"; return; }

        var result = System.Windows.MessageBox.Show(
            $"确认删除全部 {completed.Count} 个已完成任务？此操作不可撤销。",
            "删除已完成任务",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        _ipc.Send(new Command { Action = "deleteCompletedTasks" });
        if (_editingId != null && completed.Any(r => r.Id == _editingId))
            OnNew(sender, e);
        StatusText.Text = $"已删除 {completed.Count} 个已完成任务";
    }

    private void OnSelectTask(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTaskSelectionReload) return;
        if (TaskGrid.SelectedItem is not TaskRow row) return;
        _session.Write.LoadingTask = true;
        _autoSaveTimer?.Stop();

        _editingId = row.Id;
        _taskCreatedAt = row.CreatedAt ?? DateTimeOffset.Now;
        NameBox.Text = row.Name;
        ColorBox.Text = row.Color;
        SetGifPath(row.Gif, autoSave: false);
        SelectType(row.Type);
        SetDateTime(StartDatePicker, StartHourBox, StartMinuteBox,
            row.StartAt?.LocalDateTime ?? DateTime.Now);
        SetDateTime(EndDatePicker, EndHourBox, EndMinuteBox, row.EndAt.LocalDateTime);
        _editingBehaviors = row.ExpiredBehaviors; // 保留任务已有覆盖，表单不展示
        LoadRecurrence(row.Recurrence);
        SelectComboByTag(TaskPositionBox, row.Position);
        UpdateTaskActionButtons(row);
        StatusText.Text = $"正在编辑：{row.Name}";
        MainTabs.SelectedItem = TaskTab;

        _lastSavedDto = BuildCurrentDto();
        _session.Write.LoadingTask = false;
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        // 先保存当前编辑的修改，再清空表单进入新建状态。
        AutoSaveTask();

        _editingId = null;
        _taskCreatedAt = DateTimeOffset.Now;
        _lastSavedDto = null;
        NameBox.Text = "";
        ColorBox.Text = SuggestUnusedColor();
        SetGifPath("", autoSave: false);
        SelectType("scheduled");
        SetDateTime(StartDatePicker, StartHourBox, StartMinuteBox, DateTime.Now);
        SetDateTime(EndDatePicker, EndHourBox, EndMinuteBox, DateTime.Now.AddHours(1));
        _editingBehaviors = null; // 新任务默认沿用全局
        LoadRecurrence(null);    // 新任务默认不循环
        SelectComboByTag(TaskPositionBox, "");
        TaskGrid.SelectedItem = null;
        DuplicateTaskButton.Visibility = Visibility.Collapsed;
        CompleteTaskButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "新建任务";
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not TaskRow row) { StatusText.Text = "请先选择任务"; return; }
        _ipc.Send(new Command { Action = "deleteTask", TaskId = row.Id });
        OnNew(sender, e);
        StatusText.Text = "已删除";
    }

    private void OnCompleteTask(object sender, RoutedEventArgs e)
    {
        string? id = null;
        if (sender is FrameworkElement el && el.Tag is string tag) id = tag;
        else id = _editingId;

        if (string.IsNullOrEmpty(id)) return;
        CompleteTaskById(id);
    }

    private void CompleteTaskById(string id)
    {
        if (_rows.FirstOrDefault(r => r.Id == id) is not TaskRow row) return;
        if (row.Completed || _session.Write.IsPendingComplete(id))
        {
            StatusText.Text = row.Completed ? "该任务已完成" : "正在完成…";
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"确认完成任务「{row.Name}」？",
            "完成任务",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        // 暂停自动保存，避免与 completeTask 响应竞态把已完成状态覆盖回进行中。
        _autoSaveTimer?.Stop();
        _session.Write.LoadingTask = true;
        _session.Write.AddPendingComplete(id);
        _ipc.Send(new Command { Action = "completeTask", TaskId = row.Id });

        // 乐观更新列表：Headless 写盘后会单播 tasks 快照，但 UI 不能等响应才反馈。
        ReplaceRowWithCompleted(row);
        _rowsView?.Refresh();

        bool isRecurring = row.Type == "scheduled" && row.Recurrence != null &&
                           !string.IsNullOrEmpty(row.Recurrence.Mode);
        StatusText.Text = _filterMode == "active"
            ? (isRecurring ? "任务已完成并进入下一循环（已从「进行中」列表移除）" : "任务已完成（可从「已完成」筛选查看）")
            : (isRecurring ? "任务已完成并进入下一循环" : "任务已完成");

        if (_editingId == row.Id)
            UpdateTaskActionButtons(FindRowById(id) ?? row);
    }

    private TaskRow? FindRowById(string id) => _rows.FirstOrDefault(r => r.Id == id);

    private void ReplaceRowWithCompleted(TaskRow row)
    {
        var idx = _rows.IndexOf(row);
        if (idx < 0) return;
        _rows[idx] = TaskRow.AsCompleted(row);
    }

    private void UpdateTaskActionButtons(TaskRow row)
    {
        if (row.Completed)
        {
            DuplicateTaskButton.Visibility = Visibility.Visible;
            CompleteTaskButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            DuplicateTaskButton.Visibility = Visibility.Collapsed;
            CompleteTaskButton.Visibility = Visibility.Visible;
        }
    }

    private void OnDuplicateTask(object sender, RoutedEventArgs e)
    {
        if (_editingId == null) return;
        if (_rows.FirstOrDefault(r => r.Id == _editingId) is not TaskRow row) return;
        DuplicateTaskFromRow(row);
    }

    // 列表操作列「重建」按钮：等同于编辑表单的「创建为新任务」。
    private void OnRecreateTask(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string id) return;
        if (_rows.FirstOrDefault(r => r.Id == id) is not TaskRow row) return;
        DuplicateTaskFromRow(row);
    }

    private void DuplicateTaskFromRow(TaskRow row)
    {
        var result = System.Windows.MessageBox.Show(
            $"确认将已完成的任务「{row.Name}」创建为新任务？\n\n开始日期将调整为今天，截止时间按原开始日期的差值顺延。",
            "创建为新任务",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        var today = DateTime.Today;
        DateTimeOffset? newStart = null;
        DateTimeOffset newEnd;

        if (row.Type == "scheduled" && row.StartAt.HasValue)
        {
            var originalStartLocal = row.StartAt.Value.LocalDateTime;
            var adjustedStart = new DateTime(today.Year, today.Month, today.Day,
                originalStartLocal.Hour, originalStartLocal.Minute, 0, originalStartLocal.Kind);
            newStart = new DateTimeOffset(adjustedStart, TimeZoneInfo.Local.GetUtcOffset(adjustedStart));

            var dayOffset = today - originalStartLocal.Date;
            var adjustedEndLocal = row.EndAt.LocalDateTime + dayOffset;
            newEnd = new DateTimeOffset(adjustedEndLocal, TimeZoneInfo.Local.GetUtcOffset(adjustedEndLocal));
        }
        else
        {
            // 即时任务没有开始时间：以截止日期为锚点，将截止日期调整为今天。
            var originalEndLocal = row.EndAt.LocalDateTime;
            var adjustedEnd = new DateTime(today.Year, today.Month, today.Day,
                originalEndLocal.Hour, originalEndLocal.Minute, 0, originalEndLocal.Kind);
            newEnd = new DateTimeOffset(adjustedEnd, TimeZoneInfo.Local.GetUtcOffset(adjustedEnd));
        }

        var dto = new TaskDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = row.Name,
            Type = row.Type,
            Color = row.Color,
            Gif = row.Gif,
            Position = row.Position,
            StartAt = newStart,
            EndAt = newEnd,
            ExpiredBehaviors = row.ExpiredBehaviors,
            Recurrence = row.Recurrence,
            Status = "active",
            Completed = false,
            CompletedAt = null,
            CreatedAt = DateTimeOffset.Now,
        };

        _ipc.Send(new Command { Action = "createTask", Task = dto });
        StatusText.Text = $"已将「{row.Name}」创建为新任务";
    }

    private void OnBrowseGif(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择跟随图片 / 动图",
            Filter = "图片 (*.gif;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff)" +
                     "|*.gif;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) SetGifPath(dlg.FileName, autoSave: true);
    }

    private void OnClearGif(object sender, RoutedEventArgs e) => SetGifPath("", autoSave: true);

    // 设置当前图片路径：更新预览，并按需触发自动保存。
    private void SetGifPath(string? path, bool autoSave)
    {
        _gifPath = path ?? "";
        RefreshGifPreview();
        if (autoSave) TryAutoSaveTask();
    }

    // 刷新图片预览框：释放旧精灵，按需创建新精灵（30px 高，动图可动），并切换占位文字。
    private void RefreshGifPreview()
    {
        if (_gifPreviewSprite != null)
        {
            GifPreviewHost.Content = null;
            _gifPreviewSprite.Dispose();
            _gifPreviewSprite = null;
        }

        bool hasImage = Overlay.ImageSprite.IsUsable(_gifPath);
        if (hasImage)
        {
            try
            {
                _gifPreviewSprite = new Overlay.ImageSprite(_gifPath, 30);
                GifPreviewHost.Content = _gifPreviewSprite.Element;
            }
            catch
            {
                _gifPreviewSprite = null;
                hasImage = false;
            }
        }

        GifPlaceholder.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;

        // 仅在存在（可能为动图的）精灵时驱动定时器逐帧推进。
        if (_gifPreviewSprite != null)
        {
            if (_gifPreviewTimer == null)
            {
                _gifPreviewTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(50),
                };
                _gifPreviewTimer.Tick += (_, _) => _gifPreviewSprite?.Advance();
            }
            _gifPreviewTimer.Start();
        }
        else
        {
            _gifPreviewTimer?.Stop();
        }
    }

    private void OnGifDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnGifDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            SetGifPath(files[0], autoSave: true);
        }
        e.Handled = true;
    }

    private void OnTypeChanged(object sender, RoutedEventArgs e) => UpdateStartVisibility();

    private void OnColorChanged(object sender, TextChangedEventArgs e)
    {
        if (TryParseColor(ColorBox.Text, out var hex) && ColorPreview != null)
            ColorPreview.Background = new SolidColorBrush(HexToColor(hex));
        TryAutoSaveTask();
    }

    // ---- 自动保存逻辑 ----
    // 所有任务字段变更后调用 TryAutoSaveTask()；用 500ms 防抖避免每次击键都触发 IPC。
    private void HookTaskFieldEvents()
    {
        NameBox.TextChanged += (_, _) => TryAutoSaveTask();
        ColorBox.TextChanged += (_, _) => TryAutoSaveTask();
        TypeScheduledRadio.Checked += (_, _) => { UpdateStartVisibility(); TryAutoSaveTask(); };
        TypeInstantRadio.Checked += (_, _) => { UpdateStartVisibility(); TryAutoSaveTask(); };
        StartDatePicker.SelectedDateChanged += (_, _) => TryAutoSaveTask();
        EndDatePicker.SelectedDateChanged += (_, _) => TryAutoSaveTask();
        RecurModeBox.SelectionChanged += (_, _) => { UpdateRecurVisibility(); TryAutoSaveTask(); };
        RecurIntervalBox.TextChanged += (_, _) => TryAutoSaveTask();
        foreach (var chk in WeekdayChecks())
        {
            chk.Checked += (_, _) => TryAutoSaveTask();
            chk.Unchecked += (_, _) => TryAutoSaveTask();
        }
        TaskPositionBox.SelectionChanged += (_, _) => TryAutoSaveTask();
    }

    private void TryAutoSaveTask()
    {
        if (!_session.Write.CanAutoSaveTask(_session, _editingId)) return;
        if (_autoSaveTimer == null) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSaveTask()
    {
        if (!_session.Write.CanAutoSaveTask(_session, _editingId)) return;

        var dto = BuildCurrentDto();
        if (dto == null)
        {
            if (!string.IsNullOrEmpty(_buildDtoError))
                StatusText.Text = _buildDtoError;
            return;
        }

        // 表单无实际变化时不触发保存，避免选中/加载阶段误报“已更新”。
        if (_lastSavedDto != null && TaskDtoEquals(dto, _lastSavedDto)) return;

        if (!_ipc.IsConnected)
        {
            StatusText.Text = "未连接到核心进程，无法保存";
            return;
        }

        bool isNew = _editingId == null;
        _ipc.Send(new Command { Action = isNew ? "createTask" : "updateTask", Task = dto });
        StatusText.Text = isNew ? "已创建" : "已更新";
        _editingId = dto.Id;
        _lastSavedDto = dto;
    }

    private TaskDto? BuildCurrentDto()
    {
        _buildDtoError = null;
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (!TryParseColor(ColorBox.Text, out var color)) return null;
        if (!TryComposeDateTime(EndDatePicker, EndHourBox, EndMinuteBox, out var end))
        {
            _buildDtoError = "请填写有效的截止时间（时 0~23，分 0~59）";
            return null;
        }

        var gif = _gifPath.Trim();
        var position = (TaskPositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var type = SelectedType();
        DateTimeOffset? start = null;
        RecurrenceDto? recurrence = null;
        if (type == "scheduled")
        {
            if (!TryComposeDateTime(StartDatePicker, StartHourBox, StartMinuteBox, out var s))
            {
                _buildDtoError = "请填写有效的开始时间（时 0~23，分 0~59）";
                return null;
            }
            recurrence = CollectRecurrence();
            start = s;
        }

        if (!IsTaskTimeRangeValid(type, start, end, _taskCreatedAt, out var timeError))
        {
            _buildDtoError = timeError;
            return null;
        }

        // 保留正在编辑任务的完成状态，避免编辑表单把已完成任务重置为进行中。
        var existing = _editingId != null ? _rows.FirstOrDefault(r => r.Id == _editingId) : null;

        return new TaskDto
        {
            Id = _editingId ?? Guid.NewGuid().ToString(),
            Name = name,
            Type = type,
            Color = color,
            // 必须显式序列化 gif（含空串），否则 JSON 省略该字段会导致 Go 端整任务替换时清空图片。
            Gif = gif,
            ImageMaxSize = existing?.ImageMaxSize ?? 0,
            Position = position,
            StartAt = start,
            EndAt = end,
            CreatedAt = _taskCreatedAt,
            ExpiredBehaviors = CollectTaskBehaviors(),
            Recurrence = recurrence,
            Status = existing?.Status ?? "active",
            Completed = existing?.Completed ?? false,
            CompletedAt = existing?.CompletedAt,
        };
    }

    private static bool TaskDtoEquals(TaskDto a, TaskDto b)
    {
        if (a.Id != b.Id) return false;
        if (a.Name != b.Name) return false;
        if (a.Type != b.Type) return false;
        if (a.Color != b.Color) return false;
        if (!string.Equals(a.Gif ?? "", b.Gif ?? "", StringComparison.Ordinal)) return false;
        if (a.Position != b.Position) return false;
        if (a.StartAt != b.StartAt) return false;
        if (a.EndAt != b.EndAt) return false;
        if (a.Status != b.Status) return false;
        if (a.Completed != b.Completed) return false;
        if (!SequenceEqual(a.ExpiredBehaviors, b.ExpiredBehaviors)) return false;
        if (!RecurrenceEquals(a.Recurrence, b.Recurrence)) return false;
        return true;
    }

    private static bool SequenceEqual<T>(IList<T>? a, IList<T>? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i])) return false;
        return true;
    }

    private static bool RecurrenceEquals(RecurrenceDto? a, RecurrenceDto? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Mode != b.Mode) return false;
        if (a.Interval != b.Interval) return false;
        if (!SequenceEqual(a.Weekdays, b.Weekdays)) return false;
        return true;
    }

    private static string FormatCountdown(DateTimeOffset endAt)
    {
        var remaining = endAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "已到期";
        return remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays} 天 {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void OnPickColor(object sender, MouseButtonEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { AllowFullOpen = true, FullOpen = true };
        if (TryParseColor(ColorBox.Text, out var current))
        {
            var c = HexToColor(current);
            dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var picked = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        ColorBox.Text = picked; // 触发 OnColorChanged 刷新预览
        StatusText.Text = IsColorTaken(picked)
            ? "该颜色已被其他任务使用，保存前请更换"
            : StatusText.Text;
    }

    /// <summary>颜色是否已被列表中的其他任务占用（编辑态排除自身）。</summary>
    private bool IsColorTaken(string hex)
    {
        foreach (var r in _rows)
        {
            if (r.Id == _editingId) continue;
            if (string.Equals(r.Color, hex, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>新建任务时给出一个未被占用的预设色，减少撞色概率。</summary>
    private string SuggestUnusedColor()
    {
        string[] palette =
        {
            "#FF6B35", "#E53935", "#43A047", "#1E88E5", "#FDD835",
            "#8E24AA", "#00ACC1", "#F4511E", "#6D4C41", "#3949AB",
        };
        foreach (var c in palette)
            if (!IsColorTaken(c)) return c;
        return "#FF6B35";
    }

    private void UpdateStartVisibility()
    {
        bool scheduled = SelectedType() == "scheduled";
        if (StartPanel != null) StartPanel.Visibility = scheduled ? Visibility.Visible : Visibility.Collapsed;
        if (RecurPanel != null) RecurPanel.Visibility = scheduled ? Visibility.Visible : Visibility.Collapsed;
        ScheduleFitHeightToTaskEditor();
    }

    private string SelectedType() => TypeScheduledRadio.IsChecked == true ? "scheduled" : "instant";

    private void SelectType(string type)
    {
        if (type == "instant")
        {
            TypeInstantRadio.IsChecked = true;
        }
        else
        {
            TypeScheduledRadio.IsChecked = true;
        }
        UpdateStartVisibility();
    }

    private static bool TryParseColor(string s, out string hex)
    {
        s = s.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9a-fA-F]{6}$"))
        {
            hex = s.ToUpperInvariant();
            return true;
        }
        hex = "";
        return false;
    }

    private static Color HexToColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    protected override void OnClosing(CancelEventArgs e)
    {
        DesktopLog.Info("ConfigWindow OnClosing: hide to tray");
        _autoSaveTimer.Stop();
        Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(this);
        // 关闭窗口仅隐藏到托盘，不退出进程（文档 §5.3）。
        e.Cancel = true;
        Hide();
    }
}

/// <summary>DataGrid 行视图模型。</summary>
public sealed class TaskRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshProgressLabel() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressLabel)));

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "scheduled";
    public string Color { get; init; } = "#FF6B35";
    public string? Gif { get; init; }
    public int ImageMaxSize { get; init; }
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset EndAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string Status { get; init; } = "active";
    public bool Completed { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string Position { get; init; } = "";
    public List<string>? ExpiredBehaviors { get; init; }
    public RecurrenceDto? Recurrence { get; init; }

    public string TypeLabel => Type == "instant" ? "即时" : "定时";
    public DateTimeOffset? StartDisplayAt => StartAt ?? CreatedAt;
    public DateTimeOffset EndDisplayAt => EndAt;
    public string RecurLabel => FormatRecurLabel(Type, Recurrence);
    public string StatusLabel => Completed ? "已完成" : TypeLabel;
    public string ProgressLabel => TaskSchedule.GetListProgressLabel(this, DateTimeOffset.Now);
    public Brush ColorBrush
    {
        get
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Color)); }
            catch { return Brushes.Gray; }
        }
    }

    public static TaskRow From(TaskDto t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Type = t.Type,
        Color = t.Color,
        Gif = t.Gif,
        ImageMaxSize = t.ImageMaxSize,
        StartAt = t.StartAt,
        EndAt = t.EndAt,
        CreatedAt = t.CreatedAt,
        Status = string.IsNullOrEmpty(t.Status) ? (t.Completed ? "completed" : "active") : t.Status,
        Completed = t.Completed || t.Status == "completed",
        CompletedAt = t.CompletedAt,
        Position = t.Position,
        ExpiredBehaviors = t.ExpiredBehaviors,
        Recurrence = t.Recurrence,
    };

    /// <summary>将进行中行转为已完成（乐观 UI，待 Headless tasks 响应确认）。</summary>
    public static TaskRow AsCompleted(TaskRow row)
    {
        var at = DateTimeOffset.Now;
        return new TaskRow
        {
            Id = row.Id,
            Name = row.Name,
            Type = row.Type,
            Color = row.Color,
            Gif = row.Gif,
            ImageMaxSize = row.ImageMaxSize,
            StartAt = row.StartAt,
            EndAt = row.EndAt,
            CreatedAt = row.CreatedAt,
            Status = "completed",
            Completed = true,
            CompletedAt = at,
            Position = row.Position,
            ExpiredBehaviors = row.ExpiredBehaviors,
            Recurrence = row.Recurrence,
        };
    }

    private static string FormatRecurLabel(string type, RecurrenceDto? rec)
    {
        if (type != "scheduled") return "—";
        if (rec == null || string.IsNullOrEmpty(rec.Mode)) return "无";
        return rec.Mode switch
        {
            "daily" => "每天",
            "everyN" => $"每{Math.Max(2, rec.Interval)}天",
            "weekly" => FormatRecurWeekdays(rec.Weekdays),
            _ => "无",
        };
    }

    private static string FormatRecurWeekdays(List<int>? days)
    {
        if (days == null || days.Count == 0) return "按星期";
        static string Name(int d) => d switch
        {
            1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五", 6 => "六", 0 => "日", _ => "",
        };
        var names = days.Where(d => d is >= 0 and <= 6).Distinct()
            .OrderBy(d => d == 0 ? 7 : d).Select(Name).Where(n => n.Length > 0);
        return string.Concat(names);
    }
}
