using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hope.Desktop.Services;

/// <summary>
/// 更新相关的桌面端本地偏好（与后端配置解耦）：记录「已跳过的版本」。
/// 落盘于 %APPDATA%\Hope\update.json。
/// </summary>
public sealed class UpdatePrefs
{
    [JsonPropertyName("skippedVersion")] public string? SkippedVersion { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hope", "update.json");

    public static UpdatePrefs Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var p = JsonSerializer.Deserialize<UpdatePrefs>(json);
                if (p != null) return p;
            }
        }
        catch (Exception ex) { DesktopLog.Warn($"UpdatePrefs load failed: {ex.Message}"); }
        return new UpdatePrefs();
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) { DesktopLog.Warn($"UpdatePrefs save failed: {ex.Message}"); }
    }
}
