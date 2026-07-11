using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Hope.Desktop;

namespace Hope.Desktop.Ipc;

/// <summary>
/// 命名管道客户端：连接 hope-headless 的 \\.\pipe\Hope\progress，
/// 读取每秒广播的状态并发送控制命令。断线后自动重连（文档 §5.2）。
/// </summary>
public sealed class IpcClient : IDisposable
{
    private const string PipeName = @"Hope\progress";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private readonly object _writeLock = new();
    private readonly IpcConnectFailureTracker _connectFailures = new();

    /// <summary>是否已连接到 Headless 管道。</summary>
    public bool IsConnected
    {
        get { lock (_writeLock) { return _writer != null; } }
    }

    /// <summary>收到状态广播时触发（在后台线程，订阅方需自行切回 UI 线程）。</summary>
    public event Action<StateMessage>? StateReceived;

    /// <summary>收到 listTasks 响应时触发。</summary>
    public event Action<List<TaskDto>>? TasksReceived;

    /// <summary>收到 getSettings 响应时触发。</summary>
    public event Action<SettingsDto>? SettingsReceived;

    /// <summary>收到 getVersion 响应时触发。</summary>
    public event Action<string>? VersionReceived;

    /// <summary>连接状态变化。</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>长时间无法连接到后端管道时触发，参数为失败原因。</summary>
    public event Action<string>? FatalDisconnected;

    /// <summary>允许连续连接失败的最大次数；超过后桌面端放弃并提示用户。</summary>
    public int MaxConnectFailures
    {
        get => _connectFailures.MaxConnectFailures;
        set => _connectFailures.MaxConnectFailures = value;
    }

    public void Start() => _ = Task.Run(RunLoopAsync);

    private async Task RunLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, _cts.Token).ConfigureAwait(false);
                _pipe = pipe;
                lock (_writeLock)
                {
                    _writer = new StreamWriter(pipe) { AutoFlush = true };
                }
                _connectFailures.OnConnected();
                ConnectionChanged?.Invoke(true);
                DesktopLog.Info("IPC connected to hope-headless pipe");

                using var reader = new StreamReader(pipe);
                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    Dispatch(line);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                DesktopLog.Warn($"IPC connect/read error: {ex.Message}");
                if (_connectFailures.TryReportFatal(out var reason))
                    FatalDisconnected?.Invoke(reason);
            }
            finally
            {
                ConnectionChanged?.Invoke(false);
                DesktopLog.Info("IPC disconnected");
                lock (_writeLock) { _writer = null; }
                _pipe?.Dispose();
                _pipe = null;
            }

            if (!_cts.IsCancellationRequested)
                await Task.Delay(1000, _cts.Token).ContinueWith(_ => { }).ConfigureAwait(false);
        }
    }

    // 区分状态广播与 listTasks 响应（后者带 "type":"tasks"）。
    private void Dispatch(string line)
    {
        IpcMessageDispatcher.TryDispatch(
            line,
            onTasks: tasks =>
            {
                DesktopLog.Info($"IPC dispatch type=tasks count={tasks.Count}");
                TasksReceived?.Invoke(tasks);
            },
            onSettings: settings =>
            {
                DesktopLog.Info($"IPC dispatch type=settings barHeightPx={settings.BarHeightPx}");
                SettingsReceived?.Invoke(settings);
            },
            onVersion: ver =>
            {
                DesktopLog.Info($"IPC dispatch type=version version={ver}");
                VersionReceived?.Invoke(ver);
            },
            onState: msg => StateReceived?.Invoke(msg));
    }

    /// <summary>发送命令；在后台线程写入管道，避免阻塞 UI 线程。</summary>
    public void Send(Command cmd)
    {
        // 先在调用线程序列化，避免跨线程访问可变命令对象。
        string json;
        try { json = JsonSerializer.Serialize(cmd, JsonOpts); }
        catch (Exception ex)
        {
            DesktopLog.Warn($"IPC send serialize failed action={cmd.Action}: {ex.Message}");
            return;
        }

        var action = cmd.Action;
        _ = Task.Run(() => SendCore(json, action));
    }

    private void SendCore(string json, string action)
    {
        lock (_writeLock)
        {
            if (_writer == null)
            {
                DesktopLog.Warn($"IPC send dropped (not connected) action={action}");
                return;
            }
            try
            {
                DesktopLog.Info($"IPC send begin action={action}");
                _writer.WriteLine(json);
                DesktopLog.Info($"IPC send done action={action}");
            }
            catch (Exception ex) { DesktopLog.Warn($"IPC send failed action={action}: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pipe?.Dispose();
    }
}
