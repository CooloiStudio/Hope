using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hope.Desktop.Ipc;
using ComboBox = System.Windows.Controls.ComboBox;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Hope.Desktop.Views;

/// <summary>任务配置窗口：多任务 CRUD，通过 IPC 同步至 Headless（文档 §5.3）。</summary>
public partial class ConfigWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IpcClient _ipc;
    private readonly ObservableCollection<TaskRow> _rows = new();
    private string? _editingId;

    public ConfigWindow(IpcClient ipc)
    {
        InitializeComponent();

        // 跟随系统亮 / 暗主题并应用 Mica 背景（WPF-UI，文档 §5.3.2）。
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        _ipc = ipc;
        TaskGrid.ItemsSource = _rows;
        _ipc.TasksReceived += OnTasksReceived;

        PopulateTimeBoxes(StartHourBox, StartMinuteBox);
        PopulateTimeBoxes(EndHourBox, EndMinuteBox);

        Loaded += (_, _) => _ipc.Send(new Command { Action = "listTasks" });
        OnNew(this, new RoutedEventArgs());
    }

    private static void PopulateTimeBoxes(ComboBox hour, ComboBox minute)
    {
        for (int h = 0; h < 24; h++) hour.Items.Add(h.ToString("D2"));
        for (int m = 0; m < 60; m++) minute.Items.Add(m.ToString("D2"));
    }

    private void OnTasksReceived(List<TaskDto> tasks)
    {
        Dispatcher.Invoke(() =>
        {
            _rows.Clear();
            foreach (var t in tasks) _rows.Add(TaskRow.From(t));
        });
    }

    private void OnSelectTask(object sender, SelectionChangedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not TaskRow row) return;
        _editingId = row.Id;
        NameBox.Text = row.Name;
        ColorBox.Text = row.Color;
        GifBox.Text = row.Gif ?? "";
        SelectType(row.Type);
        SetDateTime(StartDatePicker, StartHourBox, StartMinuteBox,
            row.StartAt?.LocalDateTime ?? DateTime.Now);
        SetDateTime(EndDatePicker, EndHourBox, EndMinuteBox, row.EndAt.LocalDateTime);
        StatusText.Text = $"正在编辑：{row.Name}";
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        _editingId = null;
        NameBox.Text = "";
        ColorBox.Text = SuggestUnusedColor();
        GifBox.Text = "";
        SelectType("scheduled");
        SetDateTime(StartDatePicker, StartHourBox, StartMinuteBox, DateTime.Now);
        SetDateTime(EndDatePicker, EndHourBox, EndMinuteBox, DateTime.Now.AddHours(1));
        TaskGrid.SelectedItem = null;
        StatusText.Text = "新建任务";
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
        if (dlg.ShowDialog() == true) GifBox.Text = dlg.FileName;
    }

    private void OnClearGif(object sender, RoutedEventArgs e) => GifBox.Text = "";

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e) => UpdateStartVisibility();

    private void OnColorChanged(object sender, TextChangedEventArgs e)
    {
        if (ColorPreview != null && TryParseColor(ColorBox.Text, out var hex))
            ColorPreview.Background = new SolidColorBrush(HexToColor(hex));
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
    };
}
