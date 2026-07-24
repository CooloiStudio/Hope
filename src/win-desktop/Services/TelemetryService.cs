using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Hope.Desktop.Services;

/// <summary>
/// 最小化匿名遥测：向 Aptabase 上报使用事件，用于了解装机/活跃、版本与地区分布。
/// 设计原则：
///  - 隐私优先：不收集任何可识别个人信息，不自造设备 ID；用户数由服务端用每日轮换盐匿名估算。
///  - 绝不影响主流程：全程后台异步、失败静默仅记日志。
/// 协议参考 Aptabase「自建 SDK」文档：POST {host}/api/v0/events，Header 携带 App-Key。
/// </summary>
public sealed class TelemetryService
{
    // Aptabase 应用键；主机由键中的区域（US/EU）推导。
    private const string AppKey = "A-US-1917263802";
    private const string SdkVersion = "hope-desktop@0.1.0";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _host;
    private readonly string _sessionId;

    private bool _enabled;

    /// <summary>
    /// 是否启用上报。默认关闭：在读取到用户设置（allowTelemetry）之前不发送任何事件，
    /// 确保「用户取消勾选后不发送任何信息」，也避免在意图未知时抢先外发。
    /// 状态变化时写一条诊断日志，便于本地制品定位「是否根本没启用上报」。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            DesktopLog.Info($"Telemetry enabled={value} host={_host} appKey={AppKey} isDebug={IsDebug}");
        }
    }

    public TelemetryService()
    {
        _host = AppKey.Contains("EU", StringComparison.OrdinalIgnoreCase)
            ? "https://eu.aptabase.com"
            : "https://us.aptabase.com";
        _sessionId = NewSessionId();
        // 注意：Aptabase 摄取端对任意 App-Key（含无效键）都会返回 200 并静默丢弃，
        // 故「sent -> 200」不代表后台一定收到；App-Key 必须与项目实际键一致。
        DesktopLog.Info($"Telemetry init host={_host} appKey={AppKey} isDebug={IsDebug} appVersion={AppVersion()}");
    }

    /// <summary>上报一个事件（后台异步、失败静默）。props 仅允许字符串/数值。</summary>
    public void TrackEvent(string eventName, IReadOnlyDictionary<string, object>? props = null)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(eventName)) return;
        _ = Task.Run(() => SendAsync(eventName, props));
    }

    private async Task SendAsync(string eventName, IReadOnlyDictionary<string, object>? props)
    {
        try
        {
            var payload = new[]
            {
                new
                {
                    timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    sessionId = _sessionId,
                    eventName,
                    systemProps = new
                    {
                        locale = CultureInfo.CurrentUICulture.Name,
                        osName = "Windows",
                        osVersion = Environment.OSVersion.Version.ToString(),
                        isDebug = IsDebug,
                        appVersion = AppVersion(),
                        sdkVersion = SdkVersion,
                    },
                    props = props ?? new Dictionary<string, object>(),
                }
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_host}/api/v0/events")
            {
                Content = content,
            };
            req.Headers.TryAddWithoutValidation("App-Key", AppKey);

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                // 200 仅表示摄取端收下请求；若 App-Key 与项目不符，Aptabase 会静默丢弃且仍返回 200。
                DesktopLog.Info($"Telemetry POST '{eventName}' -> {(int)resp.StatusCode} (host={_host} appKey={AppKey})");
            else
                DesktopLog.Warn($"Telemetry '{eventName}' failed: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            DesktopLog.Warn($"Telemetry '{eventName}' error: {ex.Message}");
        }
    }

    // 会话 ID：epoch 秒 + 8 位随机数（Aptabase 约定格式）。
    private static string NewSessionId()
    {
        long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int rand = Random.Shared.Next(0, 100_000_000);
        return $"{epoch}{rand:00000000}";
    }

    private static string AppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    }

    private static bool IsDebug =>
#if DEBUG
        true;
#else
        false;
#endif
}
