namespace Hope.Desktop.Services;

/// <summary>
/// 倒计时任务编辑区偏好：按时长 / 按截止。缺省与空一律视为按时长。
/// 存盘时仅写出非默认值（deadline），避免旧 tasks.json 被批量补字段触发写风暴。
/// </summary>
public static class CountdownEditModes
{
    public const string Duration = "duration";
    public const string Deadline = "deadline";

    /// <summary>规范化：仅 "deadline"（忽略大小写）保留；其余（含 null/空）→ duration。</summary>
    public static string Normalize(string? value) =>
        string.Equals(value, Deadline, StringComparison.OrdinalIgnoreCase) ? Deadline : Duration;

    /// <summary>是否按截止编辑。</summary>
    public static bool IsDeadline(string? value) => Normalize(value) == Deadline;

    /// <summary>
    /// 存盘用值：按时长返回 null（JSON omit）；按截止返回 "deadline"。
    /// 定时任务应传 null。
    /// </summary>
    public static string? ForStorage(string? value, bool isInstant) =>
        isInstant && IsDeadline(value) ? Deadline : null;
}
