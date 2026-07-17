namespace Hope.Desktop;

/// <summary>
/// Desktop 诊断日志，当前会话落盘至 %APPDATA%\Hope\logs；
/// Debug/Release 使用不同文件名，每次启动将上一次日志按时间归档。
/// </summary>
internal static class DesktopLog
{
    private static readonly object Gate = new();
#if DEBUG
    private const string LogBaseName = "hope-desktop-debug";
#else
    private const string LogBaseName = "hope-desktop-release";
#endif
    private static readonly string LogDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hope", "logs");
    private static readonly string LogPath = System.IO.Path.Combine(LogDirectory, $"{LogBaseName}.log");

    internal static string LogDirectoryPath => LogDirectory;

    static DesktopLog()
    {
        ArchivePreviousLog();
    }

    private static void ArchivePreviousLog()
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogDirectory);
            if (!System.IO.File.Exists(LogPath)) return;

            var timestamp = System.IO.File.GetLastWriteTime(LogPath).ToString("yyyyMMdd-HHmmss");
            var archivePath = System.IO.Path.Combine(LogDirectory, $"{LogBaseName}-{timestamp}.log");
            for (var suffix = 1; System.IO.File.Exists(archivePath); suffix++)
                archivePath = System.IO.Path.Combine(LogDirectory, $"{LogBaseName}-{timestamp}-{suffix}.log");

            System.IO.File.Move(LogPath, archivePath);
        }
        catch
        {
            // 归档失败不影响应用启动；后续仍尝试追加当前日志。
        }
    }

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
            System.Diagnostics.Debug.WriteLine(line);
            // 磁盘写入异步化，避免调用方（含 UI / 持其它锁的线程）被 File IO 与 Gate 锁拖死。
            var path = LogPath;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    lock (Gate)
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                        System.IO.File.AppendAllText(path, line + Environment.NewLine);
                    }
                }
                catch
                {
                    // 日志本身不能抛异常
                }
            });
        }
        catch
        {
            // 日志本身不能抛异常
        }
    }
}
