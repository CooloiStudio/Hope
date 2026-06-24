using System.IO;
using System.IO.Pipes;
using System.Text.Json;

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

    /// <summary>收到状态广播时触发（在后台线程，订阅方需自行切回 UI 线程）。</summary>
    public event Action<StateMessage>? StateReceived;

    /// <summary>收到 listTasks 响应时触发。</summary>
    public event Action<List<TaskDto>>? TasksReceived;

    /// <summary>连接状态变化。</summary>
    public event Action<bool>? ConnectionChanged;

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
                ConnectionChanged?.Invoke(true);

                using var reader = new StreamReader(pipe);
                while (!_cts.IsCancellationRequested && !reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    Dispatch(line);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 连接失败，稍后重试 */ }
            finally
            {
                ConnectionChanged?.Invoke(false);
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
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "tasks")
            {
                var tasks = doc.RootElement.GetProperty("tasks").Deserialize<List<TaskDto>>(JsonOpts);
                if (tasks != null) TasksReceived?.Invoke(tasks);
                return;
            }
            var msg = JsonSerializer.Deserialize<StateMessage>(line, JsonOpts);
            if (msg != null) StateReceived?.Invoke(msg);
        }
        catch { /* 忽略损坏行 */ }
    }

    /// <summary>发送命令；未连接时静默丢弃。</summary>
    public void Send(Command cmd)
    {
        lock (_writeLock)
        {
            if (_writer == null) return;
            try
            {
                var json = JsonSerializer.Serialize(cmd, JsonOpts);
                _writer.WriteLine(json);
            }
            catch { /* 下个广播帧会反映最新状态 */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pipe?.Dispose();
    }
}
