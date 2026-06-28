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
    private ICollectionView? _rowsView;
    // 任务列表过滤模式：all=全部 / active=进行中 / completed=已完成。
    private string _filterMode = "all";
    private string? _editingId;
    // 当前编辑任务的到期提醒覆盖（表单不再暴露，保存时原样保留）；null = 继承全局。
    private List<string>? _editingBehaviors;
    private SettingsDto _settings = new();
    private bool _loadingSettings;
    private DateTimeOffset _taskCreatedAt = DateTimeOffset.Now;
    private bool _layoutReady;
    private readonly DispatcherTimer _autoSaveTimer;

    public ConfigWindow(IpcClient ipc)
    {
        DesktopLog.Info("ConfigWindow ctor: before InitializeComponent");
        // 必须在 InitializeComponent 之前初始化，因为 XAML 事件在加载期间就可能触发 TryAutoSaveTask
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autoSaveTimer.Tick += (_, _) => AutoSaveTask();
        InitializeComponent();
        DesktopLog.Info("ConfigWindow ctor: after InitializeComponent");

        AppIconHelper.ApplyWindowIcon(this);

        _ipc = ipc;
        TaskGrid.ItemsSource = _rows;
        _rowsView = System.Windows.Data.CollectionViewSource.GetDefaultView(_rows);
        _rowsView.Filter = RowMatchesFilter;
        _ipc.TasksReceived += OnTasksReceived;
        _ipc.SettingsReceived += OnSettingsReceived;

        PopulateTimeBoxes(StartHourBox, StartMinuteBox);
        PopulateTimeBoxes(EndHourBox, EndMinuteBox);

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
        BarPositionBox.SelectionChanged += OnSettingsSelectionChanged;
        BarDirectionBox.SelectionChanged += OnSettingsSelectionChanged;
        AllFourCheck.Checked += OnSettingsControlChanged;
        AllFourCheck.Unchecked += OnSettingsControlChanged;
        AdvancedPositionCheck.Checked += OnSettingsControlChanged;
        AdvancedPositionCheck.Unchecked += OnSettingsControlChanged;
        AdvancedPositionCheck.Checked += OnAdvancedPositionChanged;
        AdvancedPositionCheck.Unchecked += OnAdvancedPositionChanged;

        StatusText.SizeChanged += (_, _) => ScheduleFitHeightToTaskEditor();
        Loaded += (_, _) =>
        {
            _layoutReady = true;
            ScheduleFitHeightToTaskEditor();
        };

        AboutVersionText.Text = FormatAppVersion();

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

    /// <summary>读取程序集版本信息并格式化为 vX.Y.Z。</summary>
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
            return $"v{v}";
        }

        var av = assembly.GetName().Version;
        return av != null ? $"v{av.Major}.{av.Minor}.{av.Build}" : "v0.0.0";
    }

    /// <summary>从 Headless 拉取任务列表与全局设置（在窗口已显示后调用）。</summary>
    public void RequestRefresh()
    {
        DesktopLog.Info("ConfigWindow RequestRefresh");
        _ipc.Send(new Command { Action = "listTasks" });
        _ipc.Send(new Command { Action = "getSettings" });
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
            ImageMaxHeightBox.Value = Math.Clamp(s.ImageMaxHeightPx <= 0 ? 15 : s.ImageMaxHeightPx, 15, 30);
            RefreshValueText.Text = ((int)RefreshBox.Value).ToString();
            BarHeightValueText.Text = ((int)BarHeightBox.Value).ToString();
            ImageMaxHeightValueText.Text = ((int)ImageMaxHeightBox.Value).ToString();
            LoadGlobalBehaviors(s.ExpiredBehaviors);
            AutostartCheck.IsChecked = s.Autostart;
            ShowConfigAtRuntimeCheck.IsChecked = s.ShowConfigAtRuntime;
            SelectComboByTag(BarPositionBox, s.BarPosition);
            AdvancedPositionCheck.IsChecked = s.AdvancedPosition;
            TaskPositionRow.Visibility = s.AdvancedPosition ? Visibility.Visible : Visibility.Collapsed;
            UpdateDirectionOptions(s.BarPosition);
            SelectComboByTag(BarDirectionBox, s.BarDirection);
            AllFourCheck.IsChecked = s.AllFour;
            _loadingSettings = false;
            DesktopLog.Info("ConfigWindow.OnSettingsReceived applied to UI");
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
        if (sender == ImageMaxHeightBox) ImageMaxHeightValueText.Text = ((int)ImageMaxHeightBox.Value).ToString();
        CommitSettings();
    }

    private void OnSettingsControlChanged(object sender, RoutedEventArgs e) => CommitSettings();

    private void CommitSettings()
    {
        if (_loadingSettings) return;
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
        _settings.BarPosition = (BarPositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "top";
        _settings.BarDirection = (BarDirectionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        _settings.AdvancedPosition = AdvancedPositionCheck.IsChecked == true;
        _settings.AllFour = AllFourCheck.IsChecked == true;
        var rect = SystemParameters.WorkArea;
        _settings.ScreenWidth = rect.Width;
        _settings.ScreenHeight = rect.Height;

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
            default: // top / bottom
                BarDirectionBox.Items.Add(new ComboBoxItem { Content = "从左到右", Tag = "forward" });
                BarDirectionBox.Items.Add(new ComboBoxItem { Content = "从右到左", Tag = "reverse" });
                break;
        }

        SelectComboByTag(BarDirectionBox, selectedTag);
        if (BarDirectionBox.SelectedItem == null) SelectComboByTag(BarDirectionBox, "");
    }

    private void OnAllFourChanged(object sender, RoutedEventArgs e) => CommitSettings();

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

    private void OnShowAdvancedSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (AdvancedOptionsPanel != null)
            AdvancedOptionsPanel.Visibility = ShowAdvancedSettingsCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnResetWindowHeight(object sender, RoutedEventArgs e)
    {
        FitHeightToTaskEditor();
        SettingsStatus.Text = "已按任务编辑区重置窗口高度";
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

    private void OnTasksReceived(List<TaskDto> tasks)
    {
        DesktopLog.Info($"ConfigWindow.OnTasksReceived count={tasks.Count}");
        Dispatcher.BeginInvoke(() =>
        {
            _rows.Clear();
            foreach (var t in tasks) _rows.Add(TaskRow.From(t));
            _rowsView?.Refresh();
            DesktopLog.Info($"ConfigWindow.OnTasksReceived grid rows={_rows.Count}");
        });
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
        _ipc.Send(new Command { Action = "listTasks" });
        if (_editingId != null && completed.Any(r => r.Id == _editingId))
            OnNew(sender, e);
        StatusText.Text = $"已删除 {completed.Count} 个已完成任务";
    }

    private void OnSelectTask(object sender, SelectionChangedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not TaskRow row) return;
        _editingId = row.Id;
        _taskCreatedAt = row.CreatedAt ?? DateTimeOffset.Now;
        NameBox.Text = row.Name;
        ColorBox.Text = row.Color;
        GifBox.Text = row.Gif ?? "";
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
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        _editingId = null;
        _taskCreatedAt = DateTimeOffset.Now;
        NameBox.Text = "";
        ColorBox.Text = SuggestUnusedColor();
        GifBox.Text = "";
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
        _ipc.Send(new Command { Action = "listTasks" });
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

        if (_editingId == row.Id)
        {
            DuplicateTaskButton.Visibility = Visibility.Visible;
            CompleteTaskButton.Visibility = Visibility.Collapsed;
        }
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
        _ipc.Send(new Command { Action = "listTasks" });
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
        if (dlg.ShowDialog() == true) { GifBox.Text = dlg.FileName; TryAutoSaveTask(); }
    }

    private void OnClearGif(object sender, RoutedEventArgs e) { GifBox.Text = ""; TryAutoSaveTask(); }

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
            TryAutoSaveTask();
        }
        e.Handled = true;
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e) => UpdateStartVisibility();

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
        GifBox.TextChanged += (_, _) => TryAutoSaveTask();
        TypeBox.SelectionChanged += (_, _) => { UpdateStartVisibility(); TryAutoSaveTask(); };
        StartDatePicker.SelectedDateChanged += (_, _) => TryAutoSaveTask();
        StartHourBox.SelectionChanged += (_, _) => TryAutoSaveTask();
        StartMinuteBox.SelectionChanged += (_, _) => TryAutoSaveTask();
        EndDatePicker.SelectedDateChanged += (_, _) => TryAutoSaveTask();
        EndHourBox.SelectionChanged += (_, _) => TryAutoSaveTask();
        EndMinuteBox.SelectionChanged += (_, _) => TryAutoSaveTask();
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
        if (_loadingSettings) return;
        if (_autoSaveTimer == null) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSaveTask()
    {
        if (_loadingSettings) return;
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!TryParseColor(ColorBox.Text, out var color)) return;
        if (!TryComposeDateTime(EndDatePicker, EndHourBox, EndMinuteBox, out _)) return;

        var gif = GifBox.Text.Trim();
        var position = (TaskPositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var type = SelectedType();
        DateTimeOffset? start = null;
        RecurrenceDto? recurrence = null;
        if (type == "scheduled")
        {
            if (TryComposeDateTime(StartDatePicker, StartHourBox, StartMinuteBox, out var s))
            {
                recurrence = CollectRecurrence();
                start = s;
            }
        }

        // 保留正在编辑任务的完成状态，避免编辑表单把已完成任务重置为进行中。
        var existing = _editingId != null ? _rows.FirstOrDefault(r => r.Id == _editingId) : null;

        var dto = new TaskDto
        {
            Id = _editingId ?? Guid.NewGuid().ToString(),
            Name = name,
            Type = type,
            Color = color,
            Gif = string.IsNullOrEmpty(gif) ? null : gif,
            Position = position,
            StartAt = start,
            EndAt = TryComposeDateTime(EndDatePicker, EndHourBox, EndMinuteBox, out var end) ? end : default,
            ExpiredBehaviors = CollectTaskBehaviors(),
            Recurrence = recurrence,
            Status = existing?.Status ?? "active",
            Completed = existing?.Completed ?? false,
            CompletedAt = existing?.CompletedAt,
        };

        if (!_ipc.IsConnected)
        {
            StatusText.Text = "未连接到核心进程，无法保存";
            return;
        }

        bool isNew = _editingId == null;
        _ipc.Send(new Command { Action = isNew ? "createTask" : "updateTask", Task = dto });
        _ipc.Send(new Command { Action = "listTasks" });
        StatusText.Text = isNew ? "已创建" : "已更新";
        _editingId = dto.Id;
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
        _autoSaveTimer.Stop();
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
    public string Status { get; init; } = "active";
    public bool Completed { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string Position { get; init; } = "";
    public List<string>? ExpiredBehaviors { get; init; }
    public RecurrenceDto? Recurrence { get; init; }

    public string TypeLabel => Type == "instant" ? "即时" : "定时";
    public string StartLabel => (StartAt ?? CreatedAt)?.LocalDateTime.ToString("MM-dd HH:mm") ?? "—";
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
        Status = string.IsNullOrEmpty(t.Status) ? (t.Completed ? "completed" : "active") : t.Status,
        Completed = t.Completed || t.Status == "completed",
        CompletedAt = t.CompletedAt,
        Position = t.Position,
        ExpiredBehaviors = t.ExpiredBehaviors,
        Recurrence = t.Recurrence,
    };
}
