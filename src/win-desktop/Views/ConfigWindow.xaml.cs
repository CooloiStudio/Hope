using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hope.Desktop.Ipc;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Hope.Desktop.Views;

/// <summary>任务配置窗口：多任务 CRUD，通过 IPC 同步至 Headless（文档 §5.3）。</summary>
public partial class ConfigWindow : Window
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm";
    private readonly IpcClient _ipc;
    private readonly ObservableCollection<TaskRow> _rows = new();
    private string? _editingId;

    public ConfigWindow(IpcClient ipc)
    {
        InitializeComponent();
        _ipc = ipc;
        TaskGrid.ItemsSource = _rows;
        _ipc.TasksReceived += OnTasksReceived;

        Loaded += (_, _) => _ipc.Send(new Command { Action = "listTasks" });
        UpdateStartVisibility();
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
        StartBox.Text = row.StartAt?.ToString(TimeFormat) ?? "";
        EndBox.Text = row.EndAt.ToString(TimeFormat);
        StatusText.Text = $"正在编辑：{row.Name}";
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        _editingId = null;
        NameBox.Text = "";
        ColorBox.Text = "#FF6B35";
        GifBox.Text = "";
        SelectType("scheduled");
        StartBox.Text = DateTime.Now.ToString(TimeFormat);
        EndBox.Text = DateTime.Now.AddHours(1).ToString(TimeFormat);
        TaskGrid.SelectedItem = null;
        StatusText.Text = "新建任务";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusText.Text = "请填写任务名称"; return; }
        if (!TryParseColor(ColorBox.Text, out var color)) { StatusText.Text = "颜色格式应为 #RRGGBB"; return; }
        if (!TryParseTime(EndBox.Text, out var end)) { StatusText.Text = "截止时间格式应为 yyyy-MM-dd HH:mm"; return; }

        var type = SelectedType();
        DateTimeOffset? start = null;
        if (type == "scheduled")
        {
            if (!TryParseTime(StartBox.Text, out var s)) { StatusText.Text = "开始时间格式应为 yyyy-MM-dd HH:mm"; return; }
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

    private void UpdateStartVisibility()
    {
        bool scheduled = SelectedType() == "scheduled";
        if (StartLabel != null) StartLabel.Visibility = scheduled ? Visibility.Visible : Visibility.Collapsed;
        if (StartBox != null) StartBox.Visibility = scheduled ? Visibility.Visible : Visibility.Collapsed;
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

    private static bool TryParseTime(string s, out DateTimeOffset value)
    {
        if (DateTime.TryParseExact(s.Trim(), TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
        {
            value = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
            return true;
        }
        value = default;
        return false;
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
