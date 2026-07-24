namespace Hope.Desktop.Services;

/// <summary>
/// 墙钟日调度判定：距上次触发满 <see cref="Interval"/> 才允许再次触发。
/// 与 UI/DispatcherTimer 解耦，便于单测「跨休眠：唤醒后按真实经过时间补发当日单元」的行为。
/// 设计要点：用绝对 UTC 时刻比较，而非相对倒计时——休眠期间墙钟照常前进，
/// 因此不会像旧的 1 天 DispatcherTimer 那样在每次唤醒被清零而永不到点。
/// 非线程安全：调用方需在单一线程（UI 线程）上使用。
/// </summary>
public sealed class DailyScheduler
{
    private DateTime _lastTickUtc;

    public DailyScheduler(DateTime startUtc, TimeSpan interval)
    {
        _lastTickUtc = startUtc;
        Interval = interval;
    }

    /// <summary>两次触发之间的最小墙钟间隔。</summary>
    public TimeSpan Interval { get; }

    /// <summary>最近一次判定为「应触发」的 UTC 时刻。</summary>
    public DateTime LastTickUtc => _lastTickUtc;

    /// <summary>
    /// 到点则记账并返回 true（表示应执行一次当日单元）；否则返回 false。
    /// 单次到点只触发一次：即使休眠跨过了多个周期，唤醒后也仅补发一次，不做补火风暴。
    /// </summary>
    public bool TryFire(DateTime nowUtc)
    {
        if (nowUtc - _lastTickUtc < Interval) return false;
        _lastTickUtc = nowUtc;
        return true;
    }
}
