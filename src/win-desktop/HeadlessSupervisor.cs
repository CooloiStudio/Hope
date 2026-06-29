using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hope.Desktop;

/// <summary>
/// 监视并按需拉起 hope-headless.exe（文档 §3.3 进程互保的 Desktop→Headless 方向）。
/// </summary>
public sealed class HeadlessSupervisor : IDisposable
{
    /// <summary>连续快速退出超过阈值后触发，参数为失败原因。</summary>
    public event Action<string>? FatalFailure;

    /// <summary>允许后端连续快速退出的最大次数；超过后桌面端自动放弃拉起并退出。</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>可选：检查是否已存在可用的核心连接。若返回 true，则 supervisor 不再尝试拉起新进程。</summary>
    public Func<bool>? IsCoreReachable { get; set; }

    private readonly CancellationTokenSource _cts = new();
    private volatile bool _quitting;
    private Process? _currentProcess;
    private int _fatalReported;

    public void Start() => _ = Task.Run(LoopAsync);

    private void ReportFatal(string reason)
    {
        if (Interlocked.Exchange(ref _fatalReported, 1) == 0)
        {
            Debug.WriteLine($"[HeadlessSupervisor] FATAL: {reason}");
            FatalFailure?.Invoke(reason);
        }
    }

    private async Task LoopAsync()
    {
        var exe = ResolveHeadlessPath();
        if (exe == null)
        {
            ReportFatal("未找到 hope-headless.exe，请检查程序目录是否完整。");
            return;
        }

        int consecutiveQuickExits = 0;
        while (!_quitting && !_cts.IsCancellationRequested)
        {
            if (_fatalReported == 1) return;

            // 若已存在可用的核心连接（如手动启动的 headless），无需再拉起新进程。
            if (IsCoreReachable?.Invoke() == true)
            {
                consecutiveQuickExits = 0;
                await Task.Delay(2000, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (Process.GetProcessesByName("hope-headless").Length == 0)
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };

                    if (Debugger.IsAttached)
                    {
                        psi.RedirectStandardOutput = true;
                        psi.RedirectStandardError = true;
                        psi.ArgumentList.Add("--debug");
                    }

                    var selfPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(selfPath))
                    {
                        psi.ArgumentList.Add("--desktop");
                        psi.ArgumentList.Add(selfPath);
                    }

                    Process? proc = Process.Start(psi);
                    _currentProcess = proc;
                    if (proc != null)
                    {
                        if (Debugger.IsAttached)
                        {
                            _ = Task.Run(() => ForwardStreamAsync(proc.StandardOutput, false, _cts.Token));
                            _ = Task.Run(() => ForwardStreamAsync(proc.StandardError, true, _cts.Token));
                        }

                        // 等待一小段时间，检查进程是否因互斥量冲突而立即退出
                        try { await Task.Delay(3000, _cts.Token); } catch { }

                        if (proc.HasExited)
                        {
                            // 进程在 3 秒内退出，可能是互斥量冲突
                            TimeSpan runtime;
                            try { runtime = proc.ExitTime - proc.StartTime; }
                            catch { runtime = TimeSpan.FromSeconds(99); } // 无法获取时间，假设不是快速退出

                            if (runtime < TimeSpan.FromSeconds(5))
                            {
                                consecutiveQuickExits++;
                                Debug.WriteLine($"[HeadlessSupervisor] headless exited quickly (ran {runtime.TotalSeconds:F1}s), possible mutex conflict. consecutiveQuickExits={consecutiveQuickExits}");
                                if (consecutiveQuickExits > MaxRetryAttempts)
                                {
                                    ReportFatal($"hope-headless 连续 {MaxRetryAttempts} 次异常退出，桌面端将停止拉起并退出。请检查是否有其他实例冲突或程序文件损坏。");
                                    return;
                                }
                            }
                            else
                            {
                                consecutiveQuickExits = 0;
                            }
                        }
                        else
                        {
                            // 进程还在运行，启动成功
                            consecutiveQuickExits = 0;
                        }
                    }
                }
                else
                {
                    consecutiveQuickExits = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HeadlessSupervisor] exception: {ex.Message}");
            }

            // 如果连续快速退出，增加延迟避免日志刷屏和频繁重启
            int delayMs = consecutiveQuickExits > 3 ? 30000 : 2000;
            if (consecutiveQuickExits > 0)
            {
                Debug.WriteLine($"[HeadlessSupervisor] waiting {delayMs}ms before next retry (consecutiveQuickExits={consecutiveQuickExits})");
            }
            await Task.Delay(delayMs, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task ForwardStreamAsync(StreamReader reader, bool isError, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                var prefix = isError ? "[headless:err]" : "[headless:out]";
                Debug.WriteLine($"{prefix} {line}");
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static string? ResolveHeadlessPath()
    {
        var sameDir = Path.Combine(AppContext.BaseDirectory, "hope-headless.exe");
        if (File.Exists(sameDir)) return sameDir;

        var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "headless", "hope-headless.exe"));
        if (File.Exists(dev)) return dev;

        return null;
    }

    public void StopWatching() => _quitting = true;

    public void Dispose()
    {
        _quitting = true;
        _cts.Cancel();
        _currentProcess?.Dispose();
    }
}
