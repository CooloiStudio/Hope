using System.Text.Json;
using Hope.Desktop;

namespace Hope.Desktop.Ipc;

/// <summary>
/// 解析 Headless 管道下行 JSON 并分发到对应回调。
/// 从 <see cref="IpcClient"/> 抽出以便单测：坏 JSON 容错、type 分流、state 广播。
/// </summary>
public static class IpcMessageDispatcher
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 分发一行 JSON。解析失败时吞掉异常并返回 false（不抛到读循环）。
    /// </summary>
    public static bool TryDispatch(
        string line,
        Action<List<TaskDto>>? onTasks = null,
        Action<SettingsDto>? onSettings = null,
        Action<string>? onVersion = null,
        Action<StateMessage>? onState = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var t))
            {
                var type = t.GetString();
                if (type == "tasks")
                {
                    var tasks = doc.RootElement.GetProperty("tasks").Deserialize<List<TaskDto>>(JsonOpts);
                    if (tasks != null) onTasks?.Invoke(tasks);
                    return true;
                }
                if (type == "settings")
                {
                    var settings = doc.RootElement.GetProperty("settings").Deserialize<SettingsDto>(JsonOpts);
                    if (settings != null) onSettings?.Invoke(settings);
                    return true;
                }
                if (type == "version")
                {
                    if (doc.RootElement.TryGetProperty("version", out var v))
                    {
                        var ver = v.GetString();
                        if (ver != null) onVersion?.Invoke(ver);
                    }
                    return true;
                }
            }
            var msg = JsonSerializer.Deserialize<StateMessage>(line, JsonOpts);
            if (msg != null)
            {
                onState?.Invoke(msg);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            DesktopLog.Warn($"IPC dispatch parse error: {ex.Message}");
            return false;
        }
    }
}
