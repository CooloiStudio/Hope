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
    [JsonPropertyName("barStart")] public double BarStart { get; set; }
    [JsonPropertyName("barEnd")] public double BarEnd { get; set; }
    [JsonPropertyName("percent")] public double Percent { get; set; }
    [JsonPropertyName("fillEnd")] public double FillEnd { get; set; }
    [JsonPropertyName("endAt")] public DateTimeOffset EndAt { get; set; }
}

/// <summary>任务到期一次性事件。</summary>
public sealed class ExpiredEvent
{
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("behavior")] public string Behavior { get; set; } = "keep";
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
    [JsonPropertyName("startAt")] public DateTimeOffset? StartAt { get; set; }
    [JsonPropertyName("endAt")] public DateTimeOffset EndAt { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>用户设置，字段与 Go 端 config.Settings 对齐。</summary>
public sealed class SettingsDto
{
    [JsonPropertyName("barHeightPx")] public int BarHeightPx { get; set; } = 4;
    [JsonPropertyName("expiredBehavior")] public string ExpiredBehavior { get; set; } = "keep";
    [JsonPropertyName("refreshSec")] public int RefreshSec { get; set; } = 1;
    [JsonPropertyName("monitor")] public string Monitor { get; set; } = "primary";
    [JsonPropertyName("autostart")] public bool Autostart { get; set; }
    [JsonPropertyName("language")] public string Language { get; set; } = "zh-CN";
}
