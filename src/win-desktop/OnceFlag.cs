using System.Threading;

namespace Hope.Desktop;

/// <summary>只触发一次的标志位（Fatal 弹窗 / 放弃拉起等场景）。</summary>
public sealed class OnceFlag
{
    private int _fired;

    public bool HasFired => Volatile.Read(ref _fired) == 1;

    /// <summary>首次调用执行 <paramref name="action"/> 并返回 true；之后返回 false。</summary>
    public bool TryFire(Action action)
    {
        if (Interlocked.Exchange(ref _fired, 1) != 0) return false;
        action();
        return true;
    }
}
