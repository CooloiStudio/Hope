using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hope.Desktop;
using Hope.Desktop.Ipc;
using ComboBox = System.Windows.Controls.ComboBox;
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
    private const double EditPanelMeasureWidth = 348;
    private const double FluentTitleBarFallbackHeight = 48;

    private readonly IpcClient _ipc;
    private readonly ObservableCollection<TaskRow> _rows = new();
    private string? _editingId;
    private SettingsDto _settings = new();
    private bool _loadingSettings;
    private DateTimeOffset _taskCreatedAt = DateTimeOffset.Now;
    private readonly DispatcherTimer _previewTimer;
    private readonly System.Windows.Controls.Image _previewImage;
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

        _previewImage = new System.Windows.Controls.Image { MaxHeight = 15, Stretch = Stretch.Uniform };
        PreviewImageCanvas.Children.Add(_previewImage);
        _previewReady = true;

        PopulateTimeBoxes(StartHourBox, StartMinuteBox);
        PopulateTimeBoxes(EndHourBox, EndMinuteBox);
        for (int i = 1; i <= 10; i++)
        {
            RefreshBox.Items.Add(i.ToString());
            BarHeightBox.Items.Add(i.ToString());
        }

        RefreshBox.SelectionChanged += OnSettingsSelectionChanged;
        BarHeightBox.SelectionChanged += OnBarHeightBoxChanged;
        AutostartCheck.Checked += OnSettingsControlChanged;
        AutostartCheck.Unchecked += OnSettingsControlChanged;
        ShowConfigAtRuntimeCheck.Checked += OnSettingsControlChanged;
        ShowConfigAtRuntimeCheck.Unchecked += OnSettingsControlChanged;

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

        StatusText.SizeChanged += (_, _) => ScheduleFitHeightToTaskEditor();
        Loaded += (_, _) =>
        {
            _layoutReady = true;
            ScheduleFitHeightToTaskEditor();
        };

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
            RefreshBox.SelectedItem = Math.Clamp(s.RefreshSec, 1, 10).ToString();
            BarHeightBox.SelectedItem = Math.Clamp(s.BarHeightPx, 1, 10).ToString();
            AutostartCheck.IsChecked = s.Autostart;
            ShowConfigAtRuntimeCheck.IsChecked = s.ShowConfigAtRuntime;
            _loadingSettings = false;
            DesktopLog.Info("ConfigWindow.OnSettingsReceived applied to UI");
            UpdatePreview();
        });
    }

    private void OnBarHeightBoxChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePreview();
        if (e.AddedItems.Count > 0) CommitSettings();
    }

    private void OnSettingsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0) CommitSettings();
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

        _settings.RefreshSec = ParseIntItem(RefreshBox, _settings.RefreshSec);
        _settings.BarHeightPx = ParseIntItem(BarHeightBox, _settings.BarHeightPx);
        _settings.Autostart = AutostartCheck.IsChecked == true;
        _settings.ShowConfigAtRuntime = ShowConfigAtRuntimeCheck.IsChecked == true;

        _ipc.Send(new Command { Action = "updateSettings", Settings = _settings });
        _ipc.Send(new Command { Action = "getSettings" });
        ApplyAutostart(_settings.Autostart);
        SettingsStatus.Text = "";
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
        SelectType(row.Type);
        SetDateTime(StartDatePicker, StartHourBox, StartMinuteBox,
            row.StartAt?.LocalDateTime ?? DateTime.Now);
        SetDateTime(EndDatePicker, EndHourBox, EndMinuteBox, row.EndAt.LocalDateTime);
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
        SelectType("scheduled");
        SetDateTime(StartDatePicker, StartHourBox, StartMinuteBox, DateTime.Now);
        SetDateTime(EndDatePicker, EndHourBox, EndMinuteBox, DateTime.Now.AddHours(1));
        TaskGrid.SelectedItem = null;
        StatusText.Text = "新建任务";
        UpdatePreview();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusText.Text = "请填写任务名称"; return; }
        if (!TryParseColor(ColorBox.Text, out var color)) { StatusText.Text = "颜色格式应为 #RRGGBB"; return; }
        if (IsColorTaken(color)) { StatusText.Text = "该颜色已被其他任务使用，请选择不同颜色"; return; }
        if (!TryComposeDateTime(EndDatePicker, EndHourBox, EndMinuteBox, out var end))
        { StatusText.Text = "请选择截止日期与时间"; return; }

        var type = SelectedType();
        DateTimeOffset? start = null;
        if (type == "scheduled")
        {
            if (!TryComposeDateTime(StartDatePicker, StartHourBox, StartMinuteBox, out var s))
            { StatusText.Text = "请选择开始日期与时间"; return; }
            if (s >= end) { StatusText.Text = "开始时间须早于截止时间"; return; }
            start = s;
        }

        if (!_ipc.IsConnected)
        {
            StatusText.Text = "未连接到核心进程（hope-headless），无法保存；请确认核心已启动";
            return;
        }

        var gif = GifBox.Text.Trim();
        var dto = new TaskDto
        {
            Id = _editingId ?? Guid.NewGuid().ToString(),
            Name = name,
            Type = type,
            Color = color,
            Gif = string.IsNullOrEmpty(gif) ? null : gif,
            StartAt = start,
            EndAt = end,
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

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e) => UpdateStartVisibility();

    private void OnColorChanged(object sender, TextChangedEventArgs e)
    {
        if (TryParseColor(ColorBox.Text, out var hex) && ColorPreview != null)
            ColorPreview.Background = new SolidColorBrush(HexToColor(hex));
        UpdatePreview();
    }

    // 实时预览：条高 = barHeightPx，[0,percent] 满涂任务色，图片对齐 percent（§5.3.3 新增 2）。
    private void UpdatePreview()
    {
        // InitializeComponent 设置 ColorBox.Text 时会触发 TextChanged，此时预览控件尚未创建。
        if (!_previewReady || PreviewTrack == null || PreviewFill == null || PreviewStatus == null)
            return;
        int barH = Math.Clamp(ParseIntItem(BarHeightBox, _settings.BarHeightPx), 1, 10);
        PreviewTrack.Height = barH;
        PreviewFill.Height = barH;

        if (TryParseColor(ColorBox.Text, out var hex))
            PreviewFill.Background = new SolidColorBrush(HexToColor(hex));

        double percent = ComputePreviewPercent(out var endAt, out var timeValid);
        double trackW = PreviewTrack.ActualWidth;
        if (trackW > 0)
            PreviewFill.Width = Math.Max(0, trackW * percent / 100.0);

        var path = GifBox.Text.Trim();
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                _previewImage.Source = bmp;
            }
            catch { _previewImage.Source = null; }
        }
        else
        {
            _previewImage.Source = null;
        }

        if (trackW > 0 && _previewImage.Source != null)
        {
            _previewImage.Measure(new System.Windows.Size(double.PositiveInfinity, 15));
            double imgW = _previewImage.DesiredSize.Width;
            double frontX = trackW * percent / 100.0;
            double left = Math.Clamp(frontX - imgW / 2, 0, Math.Max(0, trackW - imgW));
            Canvas.SetLeft(_previewImage, left);
            Canvas.SetTop(_previewImage, 0);
        }

        if (!timeValid)
            PreviewStatus.Text = "请填写有效的截止（及开始）时间以预览完成度";
        else
            PreviewStatus.Text = $"{percent:0.#}%　{FormatCountdown(endAt)}";
    }

    /// <summary>与 Headless task.Percent 一致：墙钟实时，定时用开始/截止，即时用 createdAt/截止。</summary>
    private double ComputePreviewPercent(out DateTimeOffset endAt, out bool valid)
    {
        valid = false;
        endAt = default;
        if (!TryComposeDateTime(EndDatePicker, EndHourBox, EndMinuteBox, out endAt))
            return 0;
        valid = true;

        var now = DateTimeOffset.Now;
        if (now >= endAt) return 100;

        DateTimeOffset start;
        if (SelectedType() == "scheduled")
        {
            if (!TryComposeDateTime(StartDatePicker, StartHourBox, StartMinuteBox, out start))
            {
                valid = false;
                return 0;
            }
        }
        else
        {
            start = _taskCreatedAt;
        }

        var total = (endAt - start).TotalSeconds;
        if (total <= 0) return 100;
        return Math.Clamp((now - start).TotalSeconds / total * 100.0, 0, 100);
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
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset EndAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }

    public string TypeLabel => Type == "instant" ? "即时" : "定时";
    public string EndLabel => EndAt.LocalDateTime.ToString("MM-dd HH:mm");
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
        StartAt = t.StartAt,
        EndAt = t.EndAt,
        CreatedAt = t.CreatedAt,
    };
}
