namespace Hope.Desktop;

/// <summary>
/// 显示宽度：中文/全角按 2，半角按 1（近似 wcwidth）。
/// 任务名称上限：最多 16 个汉字宽（半角合计 32）。
/// </summary>
public static class TextDisplayWidth
{
    public const int MaxTaskNameWidth = 32;

    public static int Measure(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int w = 0;
        foreach (var ch in s) w += IsWideChar(ch) ? 2 : 1;
        return w;
    }

    public static bool ExceedsTaskNameLimit(string? s) => Measure(s) > MaxTaskNameWidth;

    /// <summary>截断到不超过 <see cref="MaxTaskNameWidth"/>，尽量在完整字符边界切断。</summary>
    public static string TruncateTaskName(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (Measure(s) <= MaxTaskNameWidth) return s;

        int w = 0;
        int i = 0;
        foreach (var ch in s)
        {
            int cw = IsWideChar(ch) ? 2 : 1;
            if (w + cw > MaxTaskNameWidth) break;
            w += cw;
            i++;
        }
        return s[..i];
    }

    public static bool IsWideChar(char c) =>
        c >= 0x1100 && (
            c <= 0x115F ||
            (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) ||
            (c >= 0xAC00 && c <= 0xD7A3) ||
            (c >= 0xF900 && c <= 0xFAFF) ||
            (c >= 0xFE30 && c <= 0xFE4F) ||
            (c >= 0xFF00 && c <= 0xFF60) ||
            (c >= 0xFFE0 && c <= 0xFFE6));
}
