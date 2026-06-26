using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly ObservableCollection<TaskRow> _rows = new();
    private string? _editingId;
    private SettingsDto _settings = new();
    private bool _loadingSettings;
    private DateTimeOffset _taskCreatedAt = DateTimeOffset.Now;
    private readonly DispatcherTimer _previewTimer;
    private ImageSprite? _previewSprite;
    private string? _previewSpritePath;
    private int _previewSpriteMaxSize;
    private readonly DispatcherTimer _previewGifTimer;
    private bool _previewReady;
    private bool _layoutReady;

    public ConfigWindow(IpcClient ipc)
    {
        DesktopLog.Info("ConfigWindow ctor: before InitializeComponent");
        InitializeComponent();
        DesktopLog.Info("ConfigWindow ctor: after InitializeComponent");

        AppIconHelper.ApplyWindowIcon(this);

        _ipc = ipc;
        TaskGrid.ItemsSource = _rows;
        _ipc.TasksReceived += OnTasksReceived;
        _ipc.SettingsReceived += OnSettingsReceived;

        _previewReady = true;

        PopulateTimeBoxes(StartHourBox, StartMinuteBox);
        PopulateTimeBoxes(EndHourBox, EndMinuteBox);
        foreach (var chk in WeekdayChecks())
        {
            chk.Checked += (_, _) => UpdatePreview();
            chk.Unchecked += (_, _) => UpdatePreview();
        }

        RefreshBox.ValueChanged += OnSliderValueChanged;
        BarHeightBox.ValueChanged += OnSliderValueChanged;
        ImageMaxSizeBox.ValueChanged += OnSliderValueChanged;
        GlobalModeBox.SelectionChanged += OnSettingsSelectionChanged;
        GlobalNotifyCheck.Checked += OnSettingsControlChanged;
        GlobalNotifyCheck.Unchecked += OnSettingsControlChanged;
        AutostartCheck.Checked += OnSettingsControlChanged;
        AutostartCheck.Unchecked += OnSettingsControlChanged;
        ShowConfigAtRuntimeCheck.Checked += OnSettingsControlChanged;
        ShowConfigAtRuntimeCheck.Unchecked += OnSettingsControlChanged;
        BarPositionBox.SelectionChanged += OnSettingsSelectionChanged;
        BarDirectionBox.SelectionChanged += OnSettingsSelectionChanged;
        AdvancedPositionCheck.Checked += OnSettingsControlChanged;
        AdvancedPositionCheck.Unchecked += OnSettingsControlChanged;
        AdvancedPositionCheck.Checked += OnAdvancedPositionChanged;
        AdvancedPositionCheck.Unchecked += OnAdvancedPositionChanged;

        PreviewTrack.SizeChanged += (_, _) => UpdatePreview();
        StartDatePicker.SelectedDateChanged += (_, _) => UpdatePreview();
        EndDatePicker.SelectedDateChanged += (_, _) => UpdatePreview();
        StartHourBox.SelectionChanged += (_, _) => UpdatePreview();
        StartMinuteBox.SelectionChanged += (_, _) => UpdatePreview();
        EndHourBox.SelectionChanged += (_, _) => UpdatePreview();
        EndMinuteBox.SelectionChanged += (_, _) => UpdatePreview();

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _previewTimer.Tick += (_, _) => UpdatePreview();
        _previewTimer.Start();

        _previewGifTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _previewGifTimer.Tick += (_, _) => _previewSprite?.Advance();
        _previewGifTimer.Start();

        StatusText.SizeChanged += (_, _) => ScheduleFitHeightToTaskEditor();
        Loaded += (_, _) =>
        {
            _layoutReady = true;
            ScheduleFitHeightToTaskEditor();
        };

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersionText.Text = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v0.0.0";

        ContentRendered += (_, _) => EnsureFluentBackdrop();
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

    /// <summary>从 Headless 拉取任务列表与全局设置（在窗口已显示后调用）。</summary>
    public void RequestRefresh()
    {
        DesktopLog.Info("ConfigWindow RequestRefresh");
        if (!_previewTimer.IsEnabled) _previewTimer.Start();
        if (!_previewGifTimer.IsEnabled) _previewGifTimer.Start();
        _ipc.Send(new Command { Action = "listTasks" });
        _ipc.Send(new Command { Action = "getSettings" });
        UpdatePreview();
    }

    private void OnSettingsReceived(SettingsDto s)
    {
        DesktopLog.Info($"ConfigWindow.OnSettingsReceived barHeightPx={s.BarHeightPx}");
        Dispatcher.BeginInvoke(() =>
        {
            _settings = s;
            _loadingSettings = true;
            RefreshBox.Value = Math.Clamp(s.RefreshSec, 1, 10);
            BarHeightBox.Value = Math.Clamp(s.BarHeightPx, 1, 10);
            RefreshValueText.Text = ((int)RefreshBox.Value).ToString();
            BarHeightValueText.Text = ((int)BarHeightBox.Value).ToString();
            LoadBehaviors(s.ExpiredBehaviors, GlobalModeBox, GlobalNotifyCheck);
            AutostartCheck.IsChecked = s.Autostart;
            ShowConfigAtRuntimeCheck.IsChecked = s.ShowConfigAtRuntime;
            SelectComboByTag(BarPositionBox, s.BarPosition);
            AdvancedPositionCheck.IsChecked = s.AdvancedPosition;
            TaskPositionRow.Visibility = s.AdvancedPosition ? Visibility.Visible : Visibility.Collapsed;
            UpdateDirectionOptions(s.BarPosition);
            SelectComboByTag(BarDirectionBox, s.BarDirection);
            _loadingSettings = false;
            DesktopLog.Info("ConfigWindow.OnSettingsReceived applied to UI");
            UpdatePreview();
        });
    }

    private void OnSettingsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0) CommitSettings();
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender == RefreshBox) RefreshValueText.Text = ((int)RefreshBox.Value).ToString();
        if (sender == BarHeightBox) BarHeightValueText.Text = ((int)BarHeightBox.Value).ToString();
        if (sender == ImageMaxSizeBox)
        {
            ImageMaxSizeValueText.Text = ((int)ImageMaxSizeBox.Value).ToString();
            UpdatePreview();
            return;
        }
        UpdatePreview();
        CommitSettings();
    }

    private void OnSettingsControlChanged(object sender, RoutedEventArgs e) => CommitSettings();

    private void CommitSettings()
    {
        if (_loadingSettings) return;
        if (!_ipc.IsConnected)
        {
            SettingsStatus.Text = "未连接到核心进程（hope-headless），无法保存设置";
            return;
        }

        _settings.RefreshSec = (int)RefreshBox.Value;
        _settings.BarHeightPx = (int)BarHeightBox.Value;
        _settings.ExpiredBehaviors = CollectBehaviors(GlobalModeBox, GlobalNotifyCheck);
        _settings.Autostart = AutostartCheck.IsChecked == true;
        _settings.ShowConfigAtRuntime = ShowConfigAtRuntimeCheck.IsChecked == true;
        _settings.BarPosition = (BarPositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "top";
        _settings.BarDirection = (BarDirectionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        _settings.AdvancedPosition = AdvancedPositionCheck.IsChecked == true;

        _ipc.Send(new Command { Action = "updateSettings", Settings = _settings });
        _ipc.Send(new Command { Action = "getSettings" });
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

    private void UpdateDirectionOptions(string position)
    {
        if (BarDirectionBox == null) return;
        var selected = BarDirectionBox.SelectedItem as ComboBoxItem;
        var selectedTag = selected?.Tag?.ToString() ?? "";

        BarDirectionBox.Items.Clear();
        BarDirectionBox.Items.Add(new ComboBoxItem { Content = "默认", Tag = "" });
        switch (position)
        {
            case "left":
            case "right":
                BarDirectionBox.Items.Add(new ComboBoxItem { Content = "从上到下", Tag = "forward" });
                BarDirectionBox.Items.Add(new ComboBoxItem { Content = "从下到上", Tag = "reverse" });
                break;
            case "allFour":
                BarDirectionBox.Items.Add(new ComboBoxItem { Content = "顺时针", Tag = "clockwise" });
                BarDirectionBox.Items.Add(new ComboBoxItem { Content = "逆时针", Tag = "counterClockwise" });
                break;
            default: // top / bottom
                BarDirectionBox.Items.Add(new ComboBoxItem { Content = "从左到右", Tag = "forward" });
                BarDirectionBox.Items.Add(new ComboBoxItem { Content = "从右到左", Tag = "reverse" });
                break;
        }

        SelectComboByTag(BarDirectionBox, selectedTag);
        if (BarDirectionBox.SelectedItem == null) SelectComboByTag(BarDirectionBox, "");
    }

    private void OnBarPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var pos = (BarPositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "top";
        UpdateDirectionOptions(pos);
        CommitSettings();
    }

    private void OnBarDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0) CommitSettings();
    }

    private void OnAdvancedPositionChanged(object sender, RoutedEventArgs e)
    {
        if (TaskPositionRow != null)
            TaskPositionRow.Visibility = AdvancedPositionCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnResetWindowHeight(object sender, RoutedEventArgs e)
    {
        FitHeightToTaskEditor();
        SettingsStatus.Text = "已按任务编辑区重置窗口高度";
    }

    /// <summary>按任务编辑区实测高度调整窗体高度（§5.3.3 新增 7）。</summary>
    public void FitHeightToTaskEditor()
    {
        if (!_layoutReady || TaskEditPanel == null) return;

        TaskEditPanel.UpdateLayout();
        double panelWidth = TaskEditPanel.ActualWidth > 0 ? TaskEditPanel.ActualWidth : EditPanelMeasureWidth;
        TaskEditPanel.Measure(new System.Windows.Size(panelWidth, double.PositiveInfinity));
        double editContentH = TaskEditPanel.DesiredSize.Height;

        const double tabHeaderH = 28;
        double panelMarginV = TaskEditPanel.Margin.Top + TaskEditPanel.Margin.Bottom;
        double gridMarginV = RootGrid.Margin.Top + RootGrid.Margin.Bottom;
        double leftMinH = 32 + 8 + 32 + 80; // 列表头 + 删除按钮 + 最小表格区
        double titleBarH = AppTitleBar?.ActualHeight > 0
            ? AppTitleBar.ActualHeight
            : FluentTitleBarFallbackHeight;

        double clientH = Math.Max(editContentH + tabHeaderH + panelMarginV, leftMinH) + gridMarginV + titleBarH;
        Height = Math.Max(MinFitWindowHeight, clientH);
    }

    private void ScheduleFitHeightToTaskEditor()
    {
        if (!_layoutReady) return;
        Dispatcher.BeginInvoke(FitHeightToTaskEditor, DispatcherPriority.Loaded);
    }

    private static int ParseIntItem(ComboBox box, int fallback) =>
        box.SelectedItem is string s && int.TryParse(s, out var v) ? v : fallback;

    // ===== 到期提醒（显示模式单选下拉 + 通知勾选框）=====

    private static string ModeOf(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "keep";

    private static void SelectMode(ComboBox box, string mode)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0; // 回退到 keep
    }

    // 集合 = [显示模式]（+ 勾选时追加 notify）。
    private static List<string> CollectBehaviors(ComboBox mode, CheckBox notify)
    {
        var list = new List<string> { ModeOf(mode) };
        if (notify.IsChecked == true) list.Add("notify");
        return list;
    }

    private static void LoadBehaviors(List<string>? behaviors, ComboBox mode, CheckBox notify)
    {
        var set = behaviors ?? new List<string>();
        string m = set.Contains("celebrate") ? "celebrate" : set.Contains("hide") ? "hide" : set.Contains("blink") ? "blink" : "keep";
        SelectMode(mode, m);
        notify.IsChecked = set.Contains("notify");
    }

    private const string GlobalModeTag = "global";

    // 任务到期提醒下拉变化：选「使用全局默认」时禁用并清空本任务的系统通知。
    private void OnTaskModeChanged(object sender, SelectionChangedEventArgs e) => UpdateTaskNotifyEnabled();

    private void UpdateTaskNotifyEnabled()
    {
        if (TaskNotifyCheck == null) return;
        bool useGlobal = ModeOf(TaskModeBox) == GlobalModeTag;
        if (useGlobal) TaskNotifyCheck.IsChecked = false;
        TaskNotifyCheck.IsEnabled = !useGlobal;
    }

    // 任务级到期提醒集合：选「使用全局默认」返回 null（继承全局）；否则 [显示模式]（+ 勾选追加 notify）。
    private List<string>? CollectTaskBehaviors()
    {
        if (ModeOf(TaskModeBox) == GlobalModeTag) return null;
        var list = new List<string> { ModeOf(TaskModeBox) };
        if (TaskNotifyCheck.IsChecked == true) list.Add("notify");
        return list;
    }

    // 载入任务级到期提醒：为空（新建或旧数据）时选「使用全局默认」。
    private void LoadTaskBehaviors(List<string>? behaviors)
    {
        if (behaviors == null || behaviors.Count == 0)
        {
            SelectMode(TaskModeBox, GlobalModeTag);
            TaskNotifyCheck.IsChecked = false;
        }
        else
        {
            string m = behaviors.Contains("celebrate") ? "celebrate" : behaviors.Contains("hide") ? "hide" : behaviors.Contains("blink") ? "blink" : "keep";
            SelectMode(TaskModeBox, m);
            TaskNotifyCheck.IsChecked = behaviors.Contains("notify");
        }
        UpdateTaskNotifyEnabled();
    }

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
        UpdatePreview();
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
        UpdatePreview();
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
        UpdatePreview();
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
        UpdatePreview();
        ScheduleFitHeightToTaskEditor();
    }

    private static void PopulateTimeBoxes(ComboBox hour, ComboBox minute)
    {
        for (int h = 0; h < 24; h++) hour.Items.Add(h.ToString("D2"));
        for (int m = 0; m < 60; m++) minute.Items.Add(m.ToString("D2"));
    }

    private void OnTasksReceived(List<TaskDto> tasks)
    {
        DesktopLog.Info($"ConfigWindow.OnTasksReceived count={tasks.Count}");
        Dispatcher.BeginInvoke(() =>
        {
            _rows.Clear();
            foreach (var t in tasks) _rows.Add(TaskRow.From(t));
            DesktopLog.Info($"ConfigWindow.OnTasksReceived grid rows={_rows.Count}");
        });
    }

    private void OnSelectTask(object sender, SelectionChangedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not TaskRow row) return;
        _editingId = row.Id;
        _taskCreatedAt = row.CreatedAt ?? DateTimeOffset.Now;
        NameBox.Text = row.Name;
        ColorBox.Text = row.Color;
        GifBox.Text = row.Gif ?? "";
        ImageMaxSizeBox.Value = row.ImageMaxSize > 0 ? row.ImageMaxSize : 15;
        ImageMaxSizeValueText.Text = ((int)ImageMaxSizeBox.Value).ToString();
        SelectType(row.Type);
        SetDateTime(StartDatePicker, StartHourBox, StartMinuteBox,
            row.StartAt?.LocalDateTime ?? DateTime.Now);
        SetDateTime(EndDatePicker, EndHourBox, EndMinuteBox, row.EndAt.LocalDateTime);
        LoadTaskBehaviors(row.ExpiredBehaviors);
        LoadRecurrence(row.Recurrence);
        SelectComboByTag(TaskPositionBox, row.Position);
        StatusText.Text = $"正在编辑：{row.Name}";
        UpdatePreview();
        MainTabs.SelectedItem = TaskTab; // 选中任务自动切到任务编辑 Tab（§5.3.3 新增 4）
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        _editingId = null;
        _taskCreatedAt = DateTimeOffset.Now;
        NameBox.Text = "";
        ColorBox.Text = SuggestUnusedColor();
        GifBox.Text = "";
        ImageMaxSizeBox.Value = 15;
        ImageMaxSizeValueText.Text = "15";
        SelectType("scheduled");
        SetDateTime(StartDatePicker, StartHourBox, StartMinuteBox, DateTime.Now);
        SetDateTime(EndDatePicker, EndHourBox, EndMinuteBox, DateTime.Now.AddHours(1));
        LoadTaskBehaviors(null); // 新任务默认沿用全局
        LoadRecurrence(null);    // 新任务默认不循环
        SelectComboByTag(TaskPositionBox, "");
        TaskGrid.SelectedItem = null;
        StatusText.Text = "新建任务";
        UpdatePreview();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusText.Text = "请填写任务名称"; return; }
        if (!TryParseColor(ColorBox.Text, out var color)) { StatusText.Text = "颜色格式应为 #RRGGBB"; return; }
        if (IsColorTaken(color))
        {
            var result = System.Windows.MessageBox.Show(
                "当前颜色已被其他任务使用，是否继续使用这个颜色？",
                "颜色重复",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes) return;
        }
        if (!TryComposeDateTime(EndDatePicker, EndHourBox, EndMinuteBox, out var end))
        { StatusText.Text = "请选择截止日期与时间"; return; }

        var type = SelectedType();
        DateTimeOffset? start = null;
        RecurrenceDto? recurrence = null;
        if (type == "scheduled")
        {
            if (!TryComposeDateTime(StartDatePicker, StartHourBox, StartMinuteBox, out var s))
            { StatusText.Text = "请选择开始日期与时间"; return; }
            recurrence = CollectRecurrence();
            if (recurrence == null)
            {
                // 单次任务：开始须早于截止。
                if (s >= end) { StatusText.Text = "开始时间须早于截止时间"; return; }
            }
            else if (recurrence.Mode == "weekly" &&
                     (recurrence.Weekdays == null || recurrence.Weekdays.Count == 0))
            {
                StatusText.Text = "循环「按星期」：请至少选择一天";
                return;
            }
            else if (recurrence.Mode == "everyN" &&
                     (recurrence.Interval < 2 || recurrence.Interval > 800))
            {
                StatusText.Text = "循环间隔需为 2~800（含端点）的整数";
                return;
            }
            // 循环任务仅取时分窗口（支持跨午夜），不校验整体先后。
            start = s;
        }

        if (!_ipc.IsConnected)
        {
            StatusText.Text = "未连接到核心进程（hope-headless），无法保存；请确认核心已启动";
            return;
        }

        var gif = GifBox.Text.Trim();
        var position = (TaskPositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var dto = new TaskDto
        {
            Id = _editingId ?? Guid.NewGuid().ToString(),
            Name = name,
            Type = type,
            Color = color,
            Gif = string.IsNullOrEmpty(gif) ? null : gif,
            ImageMaxSize = (int)ImageMaxSizeBox.Value,
            Position = position,
            StartAt = start,
            EndAt = end,
            ExpiredBehaviors = CollectTaskBehaviors(),
            Recurrence = recurrence,
        };

        _ipc.Send(new Command { Action = _editingId == null ? "createTask" : "updateTask", Task = dto });
        _ipc.Send(new Command { Action = "listTasks" });
        StatusText.Text = _editingId == null ? "已创建" : "已更新";
        _editingId = dto.Id;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not TaskRow row) { StatusText.Text = "请先选择任务"; return; }
        _ipc.Send(new Command { Action = "deleteTask", TaskId = row.Id });
        _ipc.Send(new Command { Action = "listTasks" });
        OnNew(sender, e);
        StatusText.Text = "已删除";
    }

    private void OnCompleteTask(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string id) return;
        if (_rows.FirstOrDefault(r => r.Id == id) is not TaskRow row) return;
        if (row.Completed) { StatusText.Text = "该任务已完成"; return; }

        var result = System.Windows.MessageBox.Show(
            $"确认完成任务「{row.Name}」？",
            "完成任务",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        _ipc.Send(new Command { Action = "completeTask", TaskId = row.Id });
        _ipc.Send(new Command { Action = "listTasks" });
        StatusText.Text = row.Recurrence != null ? "任务已完成并进入下一循环" : "任务已完成";
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
        if (dlg.ShowDialog() == true) { GifBox.Text = dlg.FileName; UpdatePreview(); }
    }

    private void OnClearGif(object sender, RoutedEventArgs e) { GifBox.Text = ""; UpdatePreview(); }

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
            GifBox.Text = files[0];
            UpdatePreview();
        }
        e.Handled = true;
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e) => UpdateStartVisibility();

    private void OnColorChanged(object sender, TextChangedEventArgs e)
    {
        if (TryParseColor(ColorBox.Text, out var hex) && ColorPreview != null)
            ColorPreview.Background = new SolidColorBrush(HexToColor(hex));
        UpdatePreview();
    }

    // 实时预览：条高 = barHeightPx，[0,percent] 满涂任务色，图片中心对齐进度前沿。
    private void UpdatePreview()
    {
        // InitializeComponent 设置 ColorBox.Text 时会触发 TextChanged，此时预览控件尚未创建。
        if (!_previewReady || PreviewTrack == null || PreviewFill == null || PreviewStatus == null)
            return;
        int barH = (int)BarHeightBox.Value;
        PreviewTrack.Height = barH;
        PreviewFill.Height = barH;

        if (TryParseColor(ColorBox.Text, out var hex))
            PreviewFill.Background = new SolidColorBrush(HexToColor(hex));

        double percent = ComputePreviewPercent(out var endAt, out var timeValid);
        double trackW = PreviewTrack.ActualWidth;
        if (trackW > 0)
            PreviewFill.Width = Math.Max(0, trackW * percent / 100.0);

        int maxSize = (int)ImageMaxSizeBox.Value;
        PreviewImageCanvas.Height = maxSize;

        var path = GifBox.Text.Trim();
        bool pathUsable = !string.IsNullOrEmpty(path) && File.Exists(path);

        // 路径/尺寸变化时释放旧的图片精灵，避免资源泄漏或旧尺寸残留。
        if (_previewSprite != null &&
            (_previewSpritePath != path || _previewSpriteMaxSize != maxSize || !pathUsable))
        {
            PreviewImageCanvas.Children.Remove(_previewSprite.Element);
            _previewSprite.Dispose();
            _previewSprite = null;
            _previewSpritePath = null;
        }

        if (pathUsable && _previewSprite == null)
        {
            try
            {
                _previewSprite = new ImageSprite(path, maxSize);
                _previewSpritePath = path;
                _previewSpriteMaxSize = maxSize;
                PreviewImageCanvas.Children.Add(_previewSprite.Element);
            }
            catch { _previewSprite = null; _previewSpritePath = null; }
        }

        if (trackW > 0 && _previewSprite != null)
        {
            double imgW = _previewSprite.Width;
            double frontX = trackW * percent / 100.0;
            // 图片中心对齐进度前沿；到右边界时停止跟随，避免图片超出画布。
            double left = frontX - imgW / 2.0;
            double maxLeft = trackW - imgW;
            if (left > maxLeft) left = maxLeft;
            Canvas.SetLeft(_previewSprite.Element, left);
            Canvas.SetTop(_previewSprite.Element, 0);
        }

        if (!timeValid)
            PreviewStatus.Text = "请填写有效的截止（及开始）时间以预览完成度";
        else
            PreviewStatus.Text = $"{percent:0.#}%　{FormatCountdown(endAt)}";
    }

    /// <summary>与 Headless task.Percent 一致：墙钟实时；循环任务取当前发生窗口。</summary>
    private double ComputePreviewPercent(out DateTimeOffset endAt, out bool valid)
    {
        valid = false;
        endAt = default;
        if (!TryComposeDateTime(EndDatePicker, EndHourBox, EndMinuteBox, out var endFull))
            return 0;

        var now = DateTimeOffset.Now;

        if (SelectedType() == "scheduled")
        {
            if (!TryComposeDateTime(StartDatePicker, StartHourBox, StartMinuteBox, out var startFull))
                return 0;

            var rec = CollectRecurrence();
            if (rec != null)
            {
                valid = true;
                if (TryRecurrenceWindow(startFull.LocalDateTime, endFull.LocalDateTime, rec, now.LocalDateTime,
                        out var ws, out var we))
                {
                    endAt = new DateTimeOffset(we, now.Offset);
                    if (now.LocalDateTime >= we) return 100;
                    var span = (we - ws).TotalSeconds;
                    return span <= 0 ? 100 : Math.Clamp((now.LocalDateTime - ws).TotalSeconds / span * 100.0, 0, 100);
                }
                endAt = endFull; // 尚未到首个发生窗口
                return 0;
            }

            valid = true;
            endAt = endFull;
            if (now >= endFull) return 100;
            var total = (endFull - startFull).TotalSeconds;
            return total <= 0 ? 100 : Math.Clamp((now - startFull).TotalSeconds / total * 100.0, 0, 100);
        }

        valid = true;
        endAt = endFull;
        if (now >= endFull) return 100;
        var t = (endFull - _taskCreatedAt).TotalSeconds;
        return t <= 0 ? 100 : Math.Clamp((now - _taskCreatedAt).TotalSeconds / t * 100.0, 0, 100);
    }

    // C# 端镜像 Go task.windowAt：返回与 now 相关的当前发生窗口（仅用于预览）。
    private static bool TryRecurrenceWindow(DateTime start, DateTime end, RecurrenceDto rec, DateTime now,
        out DateTime ws, out DateTime we)
    {
        ws = default; we = default;
        var startTOD = start.TimeOfDay;
        var endTOD = end.TimeOfDay;
        var anchor = start.Date;
        int maxBack = rec.Mode == "everyN" ? Math.Min(Math.Max(8, rec.Interval + 1), 400) : 8;
        var d0 = now.Date;
        for (int i = 0; i <= maxBack; i++)
        {
            var d = d0.AddDays(-i);
            if (d < anchor) break;
            if (!IsOccurrenceDay(rec, anchor, d)) continue;
            var s = d + startTOD;
            var e = endTOD > startTOD ? d + endTOD : d.AddDays(1) + endTOD;
            if (s <= now) { ws = s; we = e; return true; }
        }
        return false;
    }

    private static bool IsOccurrenceDay(RecurrenceDto rec, DateTime anchor, DateTime d)
    {
        if (d < anchor) return false;
        switch (rec.Mode)
        {
            case "daily": return true;
            case "everyN":
                int n = rec.Interval < 1 ? 1 : rec.Interval;
                int days = (int)Math.Round((d - anchor).TotalDays);
                return days % n == 0;
            case "weekly":
                return rec.Weekdays != null && rec.Weekdays.Contains((int)d.DayOfWeek);
            default: return false;
        }
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

    private static void SetDateTime(DatePicker date, ComboBox hour, ComboBox minute, DateTime value)
    {
        date.SelectedDate = value.Date;
        hour.SelectedItem = value.Hour.ToString("D2");
        minute.SelectedItem = value.Minute.ToString("D2");
    }

    private static bool TryComposeDateTime(DatePicker date, ComboBox hour, ComboBox minute, out DateTimeOffset value)
    {
        value = default;
        if (date.SelectedDate is not DateTime d) return false;
        if (hour.SelectedItem is not string hs || !int.TryParse(hs, out var h)) return false;
        if (minute.SelectedItem is not string ms || !int.TryParse(ms, out var m)) return false;
        var dt = new DateTime(d.Year, d.Month, d.Day, h, m, 0);
        value = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        return true;
    }

    private string SelectedType() =>
        (TypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "scheduled";

    private void SelectType(string type)
    {
        foreach (ComboBoxItem item in TypeBox.Items)
        {
            if (item.Tag?.ToString() == type) { item.IsSelected = true; break; }
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
        _previewTimer.Stop();
        _previewGifTimer.Stop();
        Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(this);
        // 关闭窗口仅隐藏到托盘，不退出进程（文档 §5.3）。
        e.Cancel = true;
        Hide();
    }
}

/// <summary>DataGrid 行视图模型。</summary>
public sealed class TaskRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "scheduled";
    public string Color { get; init; } = "#FF6B35";
    public string? Gif { get; init; }
    public int ImageMaxSize { get; init; }
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset EndAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public bool Completed { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string Position { get; init; } = "";
    public List<string>? ExpiredBehaviors { get; init; }
    public RecurrenceDto? Recurrence { get; init; }

    public string TypeLabel => Type == "instant" ? "即时" : "定时";
    public string EndLabel => EndAt.LocalDateTime.ToString("MM-dd HH:mm");
    public string StatusLabel => Completed ? "已完成" : TypeLabel;
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
        Completed = t.Completed,
        CompletedAt = t.CompletedAt,
        Position = t.Position,
        ExpiredBehaviors = t.ExpiredBehaviors,
        Recurrence = t.Recurrence,
    };
}
