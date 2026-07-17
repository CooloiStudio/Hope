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
    /// <summary>获取写锁的最长等待；超时则丢弃本条发送，避免反堵调用方。</summary>
    private static readonly TimeSpan WriteLockTimeout = TimeSpan.FromMilliseconds(200);
    /// <summary>单次 WriteLine 最长阻塞；超时则拆除管道以解除半开连接。</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);
    /// <summary>单次 ConnectAsync 等待；休眠唤醒后 headless 拉起可能稍慢。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    /// <summary>断线后重连间隔下限。</summary>
    private static readonly TimeSpan ReconnectDelayMin = TimeSpan.FromSeconds(2);
    /// <summary>断线后重连间隔上限（指数退避封顶）。</summary>
    private static readonly TimeSpan ReconnectDelayMax = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CancellationTokenSource _cts = new();
    /// <summary>当前连接会话的取消源；RequestReconnect 可单独打断 ReadLine 而不拆掉主循环。</summary>
    private CancellationTokenSource? _sessionCts;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    /// <summary>仅串行化管道写入；不用于 IsConnected 查询。</summary>
    private readonly object _writeLock = new();
    /// <summary>0=断开 1=已连接；与写锁解耦，供 UI 热路径无锁读取。</summary>
    private int _connected;
    /// <summary>非 0：下次断线重连跳过退避，立即再连（唤醒/写卡死后的恢复）。</summary>
    private int _immediateReconnect;
    private readonly IpcConnectFailureTracker _connectFailures = new();
    private int _writeStallCount;
    private long _writeStallWindowStartTicks;

    /// <summary>是否已连接到 Headless 管道（无锁；不因管道写阻塞而卡住）。</summary>
    public bool IsConnected => Volatile.Read(ref _connected) != 0;

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

    /// <summary>
    /// 短时间内多次写锁/写超时，已主动拆管并请求立即重连。
    /// 订阅方应清理可能占住 mutex 的孤儿 headless（参数为原因字符串）。
    /// </summary>
    public event Action<string>? BackendRecoveryNeeded;

    /// <summary>允许连续连接失败的最大次数；超过后桌面端放弃并提示用户。</summary>
    public int MaxConnectFailures
    {
        get => _connectFailures.MaxConnectFailures;
        set => _connectFailures.MaxConnectFailures = value;
    }

    /// <summary>
    /// 休眠唤醒等：清空失败计数并在一段时间内抑制 FatalDisconnected，让管道自行重连。
    /// </summary>
    public void NotePowerResumed(TimeSpan suppressFatalFor)
    {
        _connectFailures.ResetFailures();
        _connectFailures.SuppressFatalUntilUtc = DateTime.UtcNow.Add(suppressFatalFor);
        DesktopLog.Info($"IPC NotePowerResumed: failures reset, suppress fatal for {suppressFatalFor.TotalMinutes:0.#}m");
    }

    /// <summary>
    /// 拆除当前半开管道并在下一轮立即重连（跳过退避）。
    /// 用于休眠唤醒后管道半死、写锁超时等场景，避免继续往坏连接上堆命令。
    /// </summary>
    public void RequestReconnect(string reason)
    {
        _connectFailures.ResetFailures();
        Interlocked.Exchange(ref _immediateReconnect, 1);
        Interlocked.Exchange(ref _writeStallCount, 0);
        DesktopLog.Info($"IPC RequestReconnect reason={reason}");

        try { _sessionCts?.Cancel(); }
        catch { /* ignore */ }

        if (Monitor.TryEnter(_writeLock, TimeSpan.FromMilliseconds(500)))
        {
            try { ForceDisconnectLocked(); }
            finally { Monitor.Exit(_writeLock); }
        }
        else
        {
            // 写锁被卡住的 WriteLine 占住：不可再抢锁；直接 Dispose 管道打断读写。
            DesktopLog.Warn("IPC RequestReconnect: write lock busy, disposing pipe unlocked");
            Volatile.Write(ref _connected, 0);
            var pipe = _pipe;
            _pipe = null;
            try { pipe?.Dispose(); } catch { /* ignore */ }
        }
    }

    public void Start() => _ = Task.Run(RunLoopAsync);

    private async Task RunLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            CancellationTokenSource? sessionCts = null;
            try
            {
                sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                _sessionCts = sessionCts;
                var sessionToken = sessionCts.Token;

                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                connectCts.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
                _pipe = pipe;
                lock (_writeLock)
                {
                    _writer = new StreamWriter(pipe) { AutoFlush = true };
                }
                Volatile.Write(ref _connected, 1);
                _connectFailures.OnConnected();
                Interlocked.Exchange(ref _writeStallCount, 0);
                ConnectionChanged?.Invoke(true);
                DesktopLog.Info("IPC connected to hope-headless pipe");

                using var reader = new StreamReader(pipe);
                while (!sessionToken.IsCancellationRequested && !_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(sessionToken).ConfigureAwait(false);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    Dispatch(line);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                // 会话级取消（RequestReconnect）：正常拆管重连，不当作连接失败。
                DesktopLog.Info("IPC session canceled (reconnect requested)");
            }
            catch (Exception ex)
            {
                DesktopLog.Warn($"IPC connect/read error: {ex.Message}");
                if (_connectFailures.TryReportFatal(out var reason))
                    FatalDisconnected?.Invoke(reason);
            }
            finally
            {
                if (ReferenceEquals(_sessionCts, sessionCts))
                    _sessionCts = null;
                try { sessionCts?.Dispose(); } catch { /* ignore */ }

                Volatile.Write(ref _connected, 0);
                ConnectionChanged?.Invoke(false);
                DesktopLog.Info("IPC disconnected");
                lock (_writeLock) { _writer = null; }
                try { _pipe?.Dispose(); } catch { /* ignore */ }
                _pipe = null;
            }

            if (!_cts.IsCancellationRequested)
            {
                var immediate = Interlocked.Exchange(ref _immediateReconnect, 0) != 0;
                var delay = immediate ? TimeSpan.FromMilliseconds(200) : NextReconnectDelay();
                DesktopLog.Info(
                    $"IPC reconnect scheduled in {delay.TotalSeconds:0.#}s " +
                    $"(failures={_connectFailures.ConsecutiveFailures} immediate={immediate})");
                try { await Task.Delay(delay, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>按连续失败次数指数退避：约 2s → 3.2s → … → 15s。</summary>
    private TimeSpan NextReconnectDelay()
    {
        int n = Math.Max(0, _connectFailures.ConsecutiveFailures);
        double sec = ReconnectDelayMin.TotalSeconds * Math.Pow(1.6, Math.Min(n, 6));
        if (sec > ReconnectDelayMax.TotalSeconds) sec = ReconnectDelayMax.TotalSeconds;
        return TimeSpan.FromSeconds(sec);
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
        if (!IsConnected)
        {
            DesktopLog.Warn($"IPC send dropped (not connected) action={action}");
            return;
        }

        if (!Monitor.TryEnter(_writeLock, WriteLockTimeout))
        {
            DesktopLog.Warn($"IPC send lock timeout action={action}");
            NoteWriteStallAndMaybeRecover($"send-lock-timeout:{action}");
            return;
        }

        Exception? writeError = null;
        bool timedOut = false;
        bool notConnected = false;
        try
        {
            if (_writer == null)
            {
                notConnected = true;
                return;
            }

            var writer = _writer;
            // 持锁期间不做磁盘日志，避免 _writeLock ⊃ DesktopLog.Gate。
            var writeTask = Task.Run(() => writer.WriteLine(json));
            if (!writeTask.Wait(WriteTimeout))
            {
                timedOut = true;
                ForceDisconnectLocked();
                return;
            }

            if (writeTask.IsFaulted)
                writeError = writeTask.Exception?.GetBaseException();
        }
        catch (Exception ex)
        {
            writeError = ex;
        }
        finally
        {
            Monitor.Exit(_writeLock);
        }

        if (notConnected)
            DesktopLog.Warn($"IPC send dropped (not connected) action={action}");
        else if (timedOut)
        {
            DesktopLog.Warn($"IPC send write timeout action={action}, pipe reset");
            NoteWriteStallAndMaybeRecover($"send-write-timeout:{action}");
        }
        else if (writeError != null)
            DesktopLog.Warn($"IPC send failed action={action}: {writeError.Message}");
    }

    /// <summary>
    /// 短窗内累计写卡死；达到阈值则拆管立即重连，并通知上层清理孤儿 headless。
    /// </summary>
    private void NoteWriteStallAndMaybeRecover(string reason)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var windowStart = Interlocked.Read(ref _writeStallWindowStartTicks);
        if (windowStart == 0 || nowTicks - windowStart > TimeSpan.FromSeconds(10).Ticks)
        {
            Interlocked.Exchange(ref _writeStallWindowStartTicks, nowTicks);
            Interlocked.Exchange(ref _writeStallCount, 1);
            return;
        }

        var count = Interlocked.Increment(ref _writeStallCount);
        if (count < 2) return;

        Interlocked.Exchange(ref _writeStallCount, 0);
        Interlocked.Exchange(ref _writeStallWindowStartTicks, 0);
        RequestReconnect(reason);
        try { BackendRecoveryNeeded?.Invoke(reason); }
        catch (Exception ex) { DesktopLog.Warn($"IPC BackendRecoveryNeeded handler failed: {ex.Message}"); }
    }

    /// <summary>持写锁时拆除管道；调用方负责随后在锁外记日志。</summary>
    private void ForceDisconnectLocked()
    {
        Volatile.Write(ref _connected, 0);
        _writer = null;
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _pipe = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _sessionCts?.Cancel(); } catch { /* ignore */ }
        Volatile.Write(ref _connected, 0);
        try { _pipe?.Dispose(); } catch { /* ignore */ }
    }
}
