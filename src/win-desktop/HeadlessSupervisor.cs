using System.Diagnostics;
using System.IO;

namespace Hope.Desktop;

/// <summary>
/// 监视并按需拉起 hope-headless.exe（文档 §3.3 进程互保的 Desktop→Headless 方向）。
/// </summary>
public sealed class HeadlessSupervisor : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _quitting;

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var exe = ResolveHeadlessPath();
        if (exe == null) return; // 找不到核心时不阻塞 Desktop 自身

        while (!_quitting && !_cts.IsCancellationRequested)
        {
            try
            {
                // 已在运行则不重复拉起（Headless 自身有单实例互斥）。
                if (Process.GetProcessesByName("hope-headless").Length == 0)
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    // 传入自身路径，接通反方向互拉：Desktop 异常退出时由 Headless 重新拉起（文档 §3.3）。
                    var selfPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(selfPath))
                    {
                        psi.ArgumentList.Add("--desktop");
                        psi.ArgumentList.Add(selfPath);
                    }
                    Process.Start(psi);
                }
            }
            catch { /* 下一轮重试 */ }

            await Task.Delay(2000).ContinueWith(_ => { }).ConfigureAwait(false);
        }
    }

    private static string? ResolveHeadlessPath()
    {
        // 生产：与 Desktop 同目录。
        var sameDir = Path.Combine(AppContext.BaseDirectory, "hope-headless.exe");
        if (File.Exists(sameDir)) return sameDir;

        // 开发：Monorepo 下的 src/headless。
        var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "headless", "hope-headless.exe"));
        if (File.Exists(dev)) return dev;

        return null;
    }

    /// <summary>标记正常退出，停止互拉。</summary>
    public void StopWatching() => _quitting = true;

    public void Dispose()
    {
        _quitting = true;
        _cts.Cancel();
    }
}
