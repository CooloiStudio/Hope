using Hope.Desktop.Ipc;
using Hope.Desktop.Overlay;

namespace Hope.Desktop.State;

/// <summary>
/// Desktop 侧唯一会话状态：Headless 经 IPC 回写的设置与任务快照。
/// 仅 <see cref="App"/> 在 IPC 回调中写入；ConfigWindow 等 UI 只读订阅，避免双副本竞态。
/// </summary>
public sealed class SessionState
{
    private SettingsDto? _settings;
    private IReadOnlyList<TaskDto> _tasks = Array.Empty<TaskDto>();

    /// <summary>写入门禁：ConfigWindow 在加载/完成流程中登记，统一拦截 CommitSettings / AutoSave。</summary>
    public SessionWriteGuard Write { get; } = new();

    /// <summary>当前全局设置；未收到 getSettings 前为 null。</summary>
    public SettingsDto? Settings => _settings;

    /// <summary>当前任务列表快照。</summary>
    public IReadOnlyList<TaskDto> Tasks => _tasks;

    /// <summary>是否已至少收到一次有效 settings 响应。</summary>
    public bool SettingsHydrated { get; private set; }

    /// <summary>是否已至少收到一次 listTasks 响应。</summary>
    public bool TasksHydrated { get; private set; }

    /// <summary>设置修订序号（每次 ApplySettings 递增，供 UI 检测过期推送）。</summary>
    public int SettingsRevision { get; private set; }

    /// <summary>任务修订序号（每次 ApplyTasks 递增）。</summary>
    public int TasksRevision { get; private set; }

    /// <summary>全局设置更新（在 App UI 线程触发）。</summary>
    public event Action<SettingsDto>? SettingsChanged;

    /// <summary>任务列表更新（在 App UI 线程触发）。</summary>
    public event Action<IReadOnlyList<TaskDto>>? TasksChanged;

    /// <summary>进度条高度（px），未水合前默认 4。</summary>
    public int OverlayBarHeightPx => Math.Clamp(_settings?.BarHeightPx ?? 4, 1, 10);

    /// <summary>全局进度条所在边。</summary>
    public string OverlayBarPosition =>
        string.IsNullOrWhiteSpace(_settings?.BarPosition) ? OverlayWindow.PositionTop : _settings!.BarPosition;

    /// <summary>单条模式下的全局前进方向。</summary>
    public string OverlayBarDirection =>
        string.IsNullOrWhiteSpace(_settings?.BarDirection) ? "forward" : _settings!.BarDirection;

    /// <summary>是否四边环绕。</summary>
    public bool OverlayAllFour => _settings?.AllFour ?? false;

    /// <summary>是否允许单任务指定展示位置。</summary>
    public bool OverlayAdvancedPosition => _settings?.AdvancedPosition ?? false;

    /// <summary>是否允许单任务覆盖图片最大高度。</summary>
    public bool OverlayAdvancedImageHeight => _settings?.AdvancedImageHeight ?? false;

    /// <summary>全局图片最大高度（px，已钳制到 15–30）。</summary>
    public int OverlayImageMaxHeightPx
    {
        get
        {
            var v = _settings?.ImageMaxHeightPx ?? 15;
            return v <= 0 ? 15 : Math.Clamp(v, 15, 30);
        }
    }

    /// <summary>
    /// 解析 segment/任务图片最大高度：未开启任务级覆盖或值为 ≤0 时用全局。
    /// </summary>
    public static int ResolveImageMaxSize(bool advancedImageHeight, int taskOrSegmentSize, int globalPx)
    {
        var global = globalPx <= 0 ? 15 : Math.Clamp(globalPx, 15, 30);
        if (!advancedImageHeight) return global;
        if (taskOrSegmentSize > 0) return Math.Clamp(taskOrSegmentSize, 15, 30);
        return global;
    }

    /// <summary>按当前会话设置解析一段图片高度。</summary>
    public int ResolveImageMaxSize(int taskOrSegmentSize) =>
        ResolveImageMaxSize(OverlayAdvancedImageHeight, taskOrSegmentSize, OverlayImageMaxHeightPx);

    /// <summary>各边独立方向表（已归一化）。</summary>
    public IReadOnlyDictionary<string, string> OverlayBarDirections =>
        NormalizeBarDirections(_settings?.BarDirections);

    /// <summary>Segment 缺省 position 时回落到全局边。</summary>
    public string ResolveSegmentPosition(string? segmentPosition) =>
        string.IsNullOrWhiteSpace(segmentPosition) ? OverlayBarPosition : segmentPosition;

    /// <summary>指定边的本地前进方向。</summary>
    public string DirectionForPosition(string position)
    {
        if (OverlayAllFour) return "forward";
        if (OverlayAdvancedPosition)
        {
            var dirs = OverlayBarDirections;
            return dirs.TryGetValue(position, out var d) && !string.IsNullOrWhiteSpace(d) ? d : "forward";
        }
        if (position == OverlayBarPosition && !string.IsNullOrWhiteSpace(OverlayBarDirection))
            return OverlayBarDirection;
        return "forward";
    }

    /// <summary>应用 Headless 返回的全局设置；不触碰任务列表。</summary>
    public void ApplySettings(SettingsDto settings)
    {
        _settings = settings;
        SettingsHydrated = true;
        SettingsRevision++;
        SettingsChanged?.Invoke(settings);
    }

    /// <summary>应用 Headless 返回的任务列表；不触碰全局设置。</summary>
    public void ApplyTasks(IReadOnlyList<TaskDto> tasks)
    {
        _tasks = tasks.ToList();
        TasksHydrated = true;
        TasksRevision++;
        TasksChanged?.Invoke(_tasks);
    }

    private static Dictionary<string, string> NormalizeBarDirections(Dictionary<string, string>? loaded)
    {
        var dirs = DefaultBarDirections();
        if (loaded == null) return dirs;
        foreach (var (key, value) in loaded)
        {
            if (value is "forward" or "reverse")
                dirs[key] = value;
        }
        return dirs;
    }

    private static Dictionary<string, string> DefaultBarDirections() => new()
    {
        ["top"] = "forward",
        ["bottom"] = "forward",
        ["left"] = "forward",
        ["right"] = "forward",
    };
}

/// <summary>ConfigWindow 写入门禁：加载设置/任务或完成流程中禁止向 Headless 回写。</summary>
public sealed class SessionWriteGuard
{
    /// <summary>正在从服务端快照填充设置控件。</summary>
    public bool LoadingSettings { get; set; }

    /// <summary>正在加载任务表单或等待 completeTask 响应。</summary>
    public bool LoadingTask { get; set; }

    private readonly HashSet<string> _pendingCompleteIds = new(StringComparer.Ordinal);

    public void AddPendingComplete(string id) => _pendingCompleteIds.Add(id);

    public void RemovePendingComplete(string id) => _pendingCompleteIds.Remove(id);

    public bool IsPendingComplete(string id) => _pendingCompleteIds.Contains(id);

    public IReadOnlyList<string> PendingCompleteIdsSnapshot() => _pendingCompleteIds.ToList();

    public int PendingCompleteCount => _pendingCompleteIds.Count;

    public bool CanCommitSettings(SessionState session) =>
        session.SettingsHydrated && !LoadingSettings && !LoadingTask && _pendingCompleteIds.Count == 0;

    public bool CanAutoSaveTask(SessionState session, string? editingId) =>
        session.SettingsHydrated && !LoadingSettings && !LoadingTask &&
        (editingId == null || !_pendingCompleteIds.Contains(editingId));
}
