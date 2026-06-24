namespace Hope.Desktop;

/// <summary>Desktop 诊断日志，落盘至 %APPDATA%\Hope\logs\hope-desktop.log。</summary>
internal static class DesktopLog
{
    private static readonly object Gate = new();
    private static string? _path;

    private static string LogPath => _path ??= System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hope", "logs", "hope-desktop.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        var detail = ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Write("ERROR", detail);
    }

    public static string ThreadTag() =>
        $"tid={Environment.CurrentManagedThreadId} ui={IsUiThread()}";

    private static bool IsUiThread() =>
        System.Windows.Application.Current?.Dispatcher.CheckAccess() == true;

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{ThreadTag()}] {message}";
            System.Diagnostics.Trace.WriteLine(line);
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志本身不能抛异常
        }
    }
}
