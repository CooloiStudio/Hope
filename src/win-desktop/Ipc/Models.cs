using System.Text.Json.Serialization;

namespace Hope.Desktop.Ipc;

/// <summary>Headless 广播的顶栏状态（对应文档 §5.2 服务端→客户端结构）。</summary>
public sealed class StateMessage
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("visible")] public bool Visible { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "idle";
    [JsonPropertyName("segments")] public List<Segment> Segments { get; set; } = new();
    [JsonPropertyName("expired")] public List<ExpiredEvent>? Expired { get; set; }
}

/// <summary>顶栏上的单个色段。</summary>
public sealed class Segment
{
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#FF6B35";
    [JsonPropertyName("gif")] public string? Gif { get; set; }
    [JsonPropertyName("imageMaxSize")] public int ImageMaxSize { get; set; }
    [JsonPropertyName("barStart")] public double BarStart { get; set; }
    [JsonPropertyName("barEnd")] public double BarEnd { get; set; }
    [JsonPropertyName("percent")] public double Percent { get; set; }
    [JsonPropertyName("fillEnd")] public double FillEnd { get; set; }
    [JsonPropertyName("endAt")] public DateTimeOffset EndAt { get; set; }
    [JsonPropertyName("expired")] public bool Expired { get; set; }
    [JsonPropertyName("behaviors")] public List<string>? Behaviors { get; set; }
    [JsonPropertyName("position")] public string Position { get; set; } = "";
    [JsonPropertyName("direction")] public string Direction { get; set; } = "";
    [JsonPropertyName("imageRotation")] public double ImageRotation { get; set; }
}

/// <summary>任务到期一次性事件（供 notify 等一次性提醒；keep/blink/hide 由 Segment 持续驱动）。</summary>
public sealed class ExpiredEvent
{
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("behaviors")] public List<string>? Behaviors { get; set; }
}

/// <summary>客户端→服务端命令。</summary>
public sealed class Command
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("task")] public TaskDto? Task { get; set; }
    [JsonPropertyName("taskId")] public string? TaskId { get; set; }
    [JsonPropertyName("settings")] public SettingsDto? Settings { get; set; }
}

/// <summary>任务数据传输对象，字段与 Go 端 task.Task 对齐。</summary>
public sealed class TaskDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "scheduled"; // scheduled / instant
    [JsonPropertyName("color")] public string Color { get; set; } = "#FF6B35";
    [JsonPropertyName("gif")] public string? Gif { get; set; }
    [JsonPropertyName("imageMaxSize")] public int ImageMaxSize { get; set; }
    [JsonPropertyName("startAt")] public DateTimeOffset? StartAt { get; set; }
    [JsonPropertyName("endAt")] public DateTimeOffset EndAt { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("completed")] public bool Completed { get; set; }
    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; set; }
    // 任务级展示位置覆盖；空字符串表示沿用全局设置。
    [JsonPropertyName("position")] public string Position { get; set; } = "";
    // 任务级到期提醒覆盖；null/空 表示沿用全局默认。
    [JsonPropertyName("expiredBehaviors")] public List<string>? ExpiredBehaviors { get; set; }
    // 循环规则；null 表示单次任务（仅定时任务可用）。
    [JsonPropertyName("recurrence")] public RecurrenceDto? Recurrence { get; set; }
}

/// <summary>循环规则，字段与 Go 端 task.Recurrence 对齐。</summary>
public sealed class RecurrenceDto
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = ""; // ""/daily/everyN/weekly
    [JsonPropertyName("interval")] public int Interval { get; set; }   // everyN：≥1
    [JsonPropertyName("weekdays")] public List<int>? Weekdays { get; set; } // 0=周日 … 6=周六
}

/// <summary>用户设置，字段与 Go 端 config.Settings 对齐。</summary>
public sealed class SettingsDto
{
    [JsonPropertyName("barHeightPx")] public int BarHeightPx { get; set; } = 4;
    // 全局默认到期提醒（多选）。
    [JsonPropertyName("expiredBehaviors")] public List<string> ExpiredBehaviors { get; set; } = new() { "keep" };
    [JsonPropertyName("refreshSec")] public int RefreshSec { get; set; } = 1;
    [JsonPropertyName("monitor")] public string Monitor { get; set; } = "primary";
    [JsonPropertyName("autostart")] public bool Autostart { get; set; }
    [JsonPropertyName("showConfigAtRuntime")] public bool ShowConfigAtRuntime { get; set; }
    [JsonPropertyName("language")] public string Language { get; set; } = "zh-CN";
    [JsonPropertyName("barPosition")] public string BarPosition { get; set; } = "top";
    [JsonPropertyName("barDirection")] public string BarDirection { get; set; } = "forward";
    [JsonPropertyName("advancedPosition")] public bool AdvancedPosition { get; set; }
    [JsonPropertyName("allFour")] public bool AllFour { get; set; }
    [JsonPropertyName("screenWidth")] public double ScreenWidth { get; set; }
    [JsonPropertyName("screenHeight")] public double ScreenHeight { get; set; }
}
