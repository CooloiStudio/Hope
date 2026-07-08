// Package engine 串联配置、任务计算与 IPC 命令处理，是 Headless 的业务核心。
package engine

import (
	"crypto/rand"
	"encoding/json"
	"fmt"
	"log/slog"
	"math"
	"sort"
	"sync"
	"time"

	"hope/headless/config"
	"hope/headless/ipc"
	"hope/headless/task"
)

// buildVersion 由 main.SetVersion 注入，默认为 "dev"。
// 通过构建时 -ldflags "-X main.Version=vX.Y.Z" 设置 main.Version，
// 再由 main 调用 SetVersion 传入本包。
var buildVersion = "dev"

// SetVersion 设置构建版本号，由 main 包在启动时调用。
func SetVersion(v string) {
	if v != "" {
		buildVersion = v
	}
}

// newTaskID 生成一个新的任务 ID（UUIDv4 格式），用于循环任务完成时创建副本。
func newTaskID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		// 退化为时间戳，极少触发；仅保证唯一性即可。
		return fmt.Sprintf("task-%d", time.Now().UnixNano())
	}
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// Engine 持有运行期状态（到期信号）。
type Engine struct {
	store *config.Store
	log   *slog.Logger

	mu       sync.Mutex
	signaled map[string]bool // taskID -> 是否已发送过到期事件

	// OnQuit 在收到 quit 命令时被调用，由 main 注入以触发进程优雅退出。
	OnQuit func()
}

// New 创建 Engine。
func New(store *config.Store, log *slog.Logger) *Engine {
	return &Engine{store: store, log: log, signaled: make(map[string]bool)}
}

// ComputeState 基于当前墙钟时间计算一帧广播状态。
func (e *Engine) ComputeState() ipc.State {
	now := time.Now()
	settings, tasks := e.store.Snapshot()
	behaviorsOf := func(t *task.Task) []string { return effectiveBehaviors(t, settings) }

	var segments []task.Segment
	if settings.AllFour {
		segments = buildAllFourLayout(tasks, now, behaviorsOf, settings.BarPosition, settings.BarDirection, settings.ScreenWidth, settings.ScreenHeight)
	} else {
		positions := collectPositions(tasks, settings)
		for _, pos := range positions {
			group := filterTasksByPosition(tasks, pos, settings.BarPosition, settings.AdvancedPosition)
			layout := task.BuildLayout(group, now, behaviorsOf, pos)
			segments = append(segments, layout.Segments...)
		}
	}

	// 图片最大高度收敛为全局设置：覆盖所有带图片段的 ImageMaxSize。
	imgMax := settings.ImageMaxHeightPx
	if imgMax <= 0 {
		imgMax = 15
	}
	for i := range segments {
		if segments[i].Gif != "" {
			segments[i].ImageMaxSize = imgMax
		}
	}

	e.mu.Lock()
	events := e.collectExpiredLocked(tasks, now, behaviorsOf)
	e.mu.Unlock()

	st := ipc.State{
		Version:  1,
		Segments: segments,
		Expired:  events,
	}

	hadAny, hasActive := computeActivity(tasks, now)
	switch {
	case len(segments) > 0:
		// 含未过期段或「保持显示」的到期段时顶栏可见。
		st.State = "running"
		st.Visible = true
	case hadAny:
		st.State = "expired"
		st.Visible = false
	default:
		st.State = "idle"
		st.Visible = false
	}

	// 任意未过期任务存在时保持 running。
	if hasActive {
		st.State = "running"
	}
	return st
}

// effectiveBehaviors 返回任务生效的到期提醒：任务级覆盖优先，否则回退全局默认。
func effectiveBehaviors(t *task.Task, s config.Settings) []string {
	if len(t.ExpiredBehaviors) > 0 {
		return t.ExpiredBehaviors
	}
	return s.ExpiredBehaviors
}

// computeActivity 返回是否存在未手动完成的任务，以及是否存在已进入窗口且未过期的任务。
func computeActivity(tasks []*task.Task, now time.Time) (hadAny, hasActive bool) {
	for _, t := range tasks {
		if t.IsCompleted() {
			continue
		}
		hadAny = true
		if t.HasStarted(now) && !t.IsExpired(now) {
			hasActive = true
		}
	}
	return
}

// NextWakeDuration 在不超过 maxInterval 的前提下，返回到最近任务边界（开始/截止）的等待时长。
// 若无更近边界则返回 maxInterval。
func NextWakeDuration(tasks []*task.Task, now time.Time, maxInterval time.Duration) time.Duration {
	var nearest time.Time
	found := false
	for _, t := range tasks {
		if t.IsCompleted() {
			continue
		}
		if next := t.NextBoundaryAfter(now); !next.IsZero() {
			if !found || next.Before(nearest) {
				nearest = next
				found = true
			}
		}
	}
	if !found {
		return maxInterval
	}
	d := nearest.Sub(now)
	if d <= 0 {
		return time.Millisecond // 边界已到，尽快重算
	}
	if d < maxInterval {
		return d
	}
	return maxInterval
}

// collectPositions 返回在单位置模式下需要渲染的所有位置（含全局位置与任务级覆盖）。
func collectPositions(tasks []*task.Task, settings config.Settings) []string {
	positions := map[string]bool{settings.BarPosition: true}
	if settings.AdvancedPosition {
		for _, t := range tasks {
			if t.Position != "" {
				positions[t.Position] = true
			}
		}
	}
	order := []string{"top", "right", "bottom", "left"}
	var out []string
	for _, p := range order {
		if positions[p] {
			out = append(out, p)
		}
	}
	return out
}

// filterTasksByPosition 收集应渲染到指定位置的任务。
// 在高级位置开启时，任务按自身 Position 覆盖分组；否则全部使用全局位置。
func filterTasksByPosition(tasks []*task.Task, position, globalPosition string, advanced bool) []*task.Task {
	var out []*task.Task
	for _, t := range tasks {
		if t.IsCompleted() {
			continue
		}
		taskPos := t.Position
		if taskPos == "" {
			taskPos = globalPosition
		}
		if advanced {
			if taskPos == position {
				out = append(out, t)
			}
		} else if globalPosition == position {
			out = append(out, t)
		}
	}
	return out
}

// buildAllFourLayout 将每个任务的进度按屏幕物理周长映射到四边。
// startPos 为 BarPosition（用户选择的基础位置），baseDir 为 BarDirection（forward/reverse）。
//
// 环绕方向由起点+填充方向共同决定（非 forward=顺时针）：
//   top+forward  → 顺时针（上右下左）
//   top+reverse  → 逆时针（上左下右）
//   bottom+forward → 逆时针（下右上左）
//   bottom+reverse → 顺时针（下左下右）
//   left+forward   → 逆时针（左下底右）
//   left+reverse   → 顺时针（左上右底）
//   right+forward  → 逆时针（右上底左）
//   right+reverse  → 顺时针（右下左底）
//
// 已填满的边生成满段（BarEnd=100，不挂图片）；
// 当前活跃边生成部分填充段（挂图片）；
// 未到达的边不生成 Segment。
func buildAllFourLayout(tasks []*task.Task, now time.Time, behaviorsOf func(*task.Task) []string, startPos, baseDir string, screenWidth, screenHeight float64) []task.Segment {
	if screenWidth <= 0 || screenHeight <= 0 {
		screenWidth, screenHeight = 1920, 1080
	}

	sides := deriveAllFourOrders(startPos, baseDir)
	surroundDir := deriveSurroundDir(startPos, baseDir)

	w, h := screenWidth, screenHeight

	// cum[i] = 第 i 条边起点在周长上的偏移，按实际 sides 顺序计算
	cum := make([]float64, 5)
	cum[0] = 0
	for i := 0; i < 4; i++ {
		cum[i+1] = cum[i] + edgeLen(sides[i], w, h)
	}
	perim := cum[4]

	// 先收集可见任务及其进度，再按 pct 降序生成段：
	// 四边环绕下每个任务都从起点画到自身 pct，重叠区由绘制顺序（z 序）决定；
	// 桌面端后绘制者在上层，故低进度段需最后 append，才能「低任务进度覆盖高任务进度」。
	type afItem struct {
		t       *task.Task
		pct     float64
		expired bool
		endAt   time.Time
		bs      []string
	}
	var items []afItem
	for _, t := range tasks {
		if t.IsCompleted() {
			continue
		}
		bs := behaviorsOf(t)
		if !t.HasStarted(now) {
			continue
		}

		var pct float64
		var expired bool
		endAt := t.EffectiveEnd(now)
		if t.IsExpired(now) {
			if !task.KeepsVisibleWhenExpired(bs) {
				continue
			}
			pct = 100
			expired = true
		} else {
			pct = t.RenderPercent(now)
		}
		items = append(items, afItem{t, pct, expired, endAt, bs})
	}
	// 降序：高进度先画（在底层），低进度后画（在上层）覆盖之。
	sort.SliceStable(items, func(i, j int) bool { return items[i].pct > items[j].pct })

	var out []task.Segment

	for _, it := range items {
		t := it.t
		pct := it.pct
		expired := it.expired
		endAt := it.endAt
		bs := it.bs

		x := perim * pct / 100.0

		// 找到当前活跃边 idx
		// 使用 <：当 x 恰好等于 cum[i+1]（当前边刚填满）时，
		// x < cum[i+1] 不成立，循环继续，activeIdx 指向下一条边（localEnd=0）。
		// 下一帧 x 略大于 cum[i+1]，localEnd > 0，新边自然出现。
		activeIdx := 3 // pct=100 时 x=perim，归为最后一条边
		for i := 0; i < 4; i++ {
			if x < cum[i+1] {
				activeIdx = i
				break
			}
		}

		for i := 0; i <= activeIdx; i++ {
			pos := sides[i]
			isActive := i == activeIdx

			localStart := 0.0
			localEnd := 100.0
			if isActive {
				localEnd = ((x - cum[i]) / edgeLen(pos, w, h)) * 100.0
				if localEnd < 0 {
					localEnd = 0
				}
				if localEnd > 100 {
					localEnd = 100
				}
			}

			seg := task.Segment{
				TaskID:        t.ID,
				Name:          t.Name,
				Color:         t.Color,
				Gif:           "",
				ImageMaxSize:  t.ImageMaxSize,
				BarStart:      round1(localStart),
				BarEnd:        round1(localEnd),
				Percent:       round1(localEnd),
				FillEnd:       round1(localEnd),
				EndAt:         endAt,
				Expired:       expired,
				Behaviors:     bs,
				Position:      pos,
				Direction:     localDirection(pos, surroundDir),
				ImageRotation: rotationForSurround(surroundDir, i),
			}
			if isActive {
				seg.Gif = t.Gif
			}
			out = append(out, seg)
		}
	}
	return out
}

// deriveAllFourOrders 返回以 startPos 为起点的四边环绕顺序。
// 环绕方向由起点+填充方向共同决定，而非 forward=顺时针。
//
// 判断规则：填充方向决定"溢出端点"，溢出后自然走向决定顺时针/逆时针。
//   top+forward（左→右，溢出右端点）→ 继续沿右边缘向下 = 顺时针
//   top+reverse（右→左，溢出左端点）→ 继续沿左边缘向下 = 逆时针
//   bottom+forward（左→右，溢出右端点）→ 继续沿右边缘向上 = 逆时针
//   bottom+reverse（右→左，溢出左端点）→ 继续沿左边缘向上 = 顺时针
//   left+forward（上→下，溢出下端点）→ 继续沿底边缘向右 = 逆时针
//   left+reverse（下→上，溢出上端点）→ 继续沿顶边缘向右 = 顺时针
//   right+forward（上→下，溢出下端点）→ 继续沿底边缘向左 = 逆时针
//   right+reverse（下→上，溢出上端点）→ 继续沿顶边缘向左 = 顺时针
func deriveAllFourOrders(startPos, baseDir string) []string {
	surroundDir := deriveSurroundDir(startPos, baseDir)

	var baseOrder []string
	if surroundDir == "cw" {
		baseOrder = []string{"top", "right", "bottom", "left"}
	} else {
		baseOrder = []string{"top", "left", "bottom", "right"}
	}

	// 旋转使 startPos 位于 [0]
	offset := 0
	for i, p := range baseOrder {
		if p == startPos {
			offset = i
			break
		}
	}
	rotated := make([]string, 4)
	for i := 0; i < 4; i++ {
		rotated[i] = baseOrder[(i+offset)%4]
	}
	return rotated
}

// deriveSurroundDir 根据起点位置和填充方向判断环绕方向。
// 返回 "cw"（顺时针）或 "ccw"（逆时针）。
//
// 判断规则：看填充方向的"溢出端点"后，进度条自然继续走向哪条边。
//   - top, forward（左→右，溢出右端点）→ 继续沿右边向下 = 顺时针
//   - top, reverse（右→左，溢出左端点）→ 继续沿左边向下 = 逆时针
//   - bottom, forward（左→右，溢出右端点）→ 继续沿右边向上 = 逆时针
//   - bottom, reverse（右→左，溢出左端点）→ 继续沿左边向上 = 顺时针
//   - left, forward（上→下，溢出下端点）→ 继续沿底边向右 = 逆时针
//   - left, reverse（下→上，溢出上端点）→ 继续沿顶边向右 = 顺时针
//   - right, forward（上→下，溢出下端点）→ 继续沿底边向左 = 顺时针
//   - right, reverse（下→上，溢出上端点）→ 继续沿顶边向左 = 逆时针
func deriveSurroundDir(startPos, fillDir string) string {
	switch startPos {
	case "top":
		if fillDir == "forward" {
			return "cw"
		}
		return "ccw"
	case "bottom":
		if fillDir == "forward" {
			return "ccw"
		}
		return "cw"
	case "left":
		if fillDir == "forward" {
			return "ccw"
		}
		return "cw"
	case "right":
		if fillDir == "forward" {
			return "cw"
		}
		return "ccw"
	default:
		return "cw"
	}
}

// edgeLen 返回一条边的物理像素长度。
func edgeLen(side string, w, h float64) float64 {
	if side == "top" || side == "bottom" {
		return w
	}
	return h
}

// rotationForSurround 返回环绕顺序中第 idx 条边的图片旋转角度（度）。
// 规则：起始边（idx=0）保持原图朝向（0°），之后每进入下一条边沿环绕方向再转 90°。
//   顺时针(cw)：0, 90, 180, 270
//   逆时针(ccw)：0, 270, 180, 90
// 即旋转量只取决于「从起点出发走过几条边」与环绕方向，与边的绝对位置无关。
func rotationForSurround(surroundDir string, idx int) float64 {
	step := 90.0
	if surroundDir != "cw" {
		step = -90.0
	}
	a := math.Mod(step*float64(idx), 360)
	if a < 0 {
		a += 360
	}
	return a
}

// localDirection 返回某条边在给定环绕方向下的本地填充方向。
// 顺时针：top=forward, right=forward, bottom=reverse, left=reverse。
// 逆时针：top=reverse, left=forward, bottom=forward, right=reverse。
func localDirection(side, surroundDir string) string {
	if surroundDir == "cw" {
		switch side {
		case "top":
			return "forward"
		case "right":
			return "forward"
		case "bottom":
			return "reverse"
		case "left":
			return "reverse"
		}
	} else {
		switch side {
		case "top":
			return "reverse"
		case "left":
			return "forward"
		case "bottom":
			return "forward"
		case "right":
			return "reverse"
		}
	}
	return "forward"
}

func clamp(v, lo, hi float64) float64 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

func round1(v float64) float64 { return math.Round(v*10) / 10 }

// collectExpiredLocked 返回自上次以来新到期的任务事件（一次性，供一次性提醒如 notify）。调用方需持有锁。
func (e *Engine) collectExpiredLocked(tasks []*task.Task, now time.Time, behaviorsOf func(*task.Task) []string) []ipc.ExpiredEvent {
	var out []ipc.ExpiredEvent
	for _, t := range tasks {
		if t.IsCompleted() {
			// 用户手动完成的任务不再触发到期通知与持续到期表现。
			delete(e.signaled, t.ID)
			continue
		}
		if !t.IsExpired(now) {
			// 未到期（含循环任务回到进行中）：清除标记，使下次到期可再次提醒。
			delete(e.signaled, t.ID)
			continue
		}
		if !e.signaled[t.ID] {
			e.signaled[t.ID] = true
			out = append(out, ipc.ExpiredEvent{
				TaskID:    t.ID,
				Name:      t.Name,
				Behaviors: behaviorsOf(t),
			})
		}
	}
	return out
}

// HandleCommand 处理一条来自 Desktop 的命令；返回值非 nil 时单播给该客户端。
func (e *Engine) HandleCommand(cmd ipc.Command) any {
	switch cmd.Action {
	case "createTask", "updateTask":
		if cmd.Task != nil {
			if cmd.Action == "updateTask" {
				preserveCompletedTask(cmd.Task, e.store)
			}
			if cmd.Task.CreatedAt.IsZero() {
				if cmd.Action == "updateTask" {
					_, tasks := e.store.Snapshot()
					for _, ex := range tasks {
						if ex.ID == cmd.Task.ID && !ex.CreatedAt.IsZero() {
							cmd.Task.CreatedAt = ex.CreatedAt
							break
						}
					}
				}
				if cmd.Task.CreatedAt.IsZero() {
					cmd.Task.CreatedAt = time.Now()
				}
			}
			e.store.UpsertTask(cmd.Task)
			e.clearSignal(cmd.Task.ID)
		}
		return e.tasksResponse(cmd.RequestID)
	case "deleteTask":
		if cmd.TaskID != "" {
			e.store.DeleteTask(cmd.TaskID)
			e.clearSignal(cmd.TaskID)
		}
		return e.tasksResponse(cmd.RequestID)
	case "completeTask":
		if cmd.TaskID != "" {
			_, tasks := e.store.Snapshot()
			for _, t := range tasks {
				if t.ID == cmd.TaskID {
					if t.IsCompleted() {
						break
					}
					now := time.Now()
					// 循环任务：先生成下一期副本（复制全部参数，起止时间推进到下一发生窗口），
					// 副本为「进行中」且拥有全新 ID；随后将当前任务标记为已完成。
					if t.IsRecurring() {
						next := t.Clone()
						if next.AdvanceIfRecurring(now) {
							next.ID = newTaskID()
							next.CreatedAt = now
							next.Status = task.StatusActive
							next.Completed = false
							next.CompletedAt = nil
							e.store.UpsertTask(next)
							e.clearSignal(next.ID)
						}
					}
					// 当前任务标记为已完成，不再渲染。
					t.Status = task.StatusCompleted
					t.Completed = true
					completedAt := now
					t.CompletedAt = &completedAt
					e.store.UpsertTask(t)
					e.clearSignal(cmd.TaskID)
					break
				}
			}
		}
		return e.tasksResponse(cmd.RequestID)
	case "deleteCompletedTasks":
		for _, id := range e.store.DeleteCompletedTasks() {
			e.clearSignal(id)
		}
		return e.tasksResponse(cmd.RequestID)
	case "listTasks":
		return e.tasksResponse(cmd.RequestID)
	case "getVersion":
		return map[string]any{"type": "version", "version": buildVersion}
	case "getSettings":
		return e.settingsResponse(cmd.RequestID)
	case "requestState":
		// Desktop 休眠唤醒等场景下立即按墙钟重算一帧，避免等下一轮定时广播。
		return e.ComputeState()
	case "updateSettings":
		if len(cmd.Settings) > 0 {
			var ns config.Settings
			if err := json.Unmarshal(cmd.Settings, &ns); err == nil {
				e.store.UpdateSettings(ns)
			}
		}
		return e.settingsResponse(cmd.RequestID)
	case "screenSize":
		// 仅上报屏幕尺寸，独立于设置合并，避免默认值覆盖用户设置。
		if len(cmd.Settings) > 0 {
			var ns config.Settings
			if err := json.Unmarshal(cmd.Settings, &ns); err == nil {
				e.store.SetScreen(ns.ScreenWidth, ns.ScreenHeight)
			}
		}
	case "quit":
		if e.OnQuit != nil {
			go e.OnQuit()
		}
		return map[string]any{"type": "quitAck"}
	default:
		e.log.Warn("unknown action", "action", cmd.Action)
	}
	return nil
}

func (e *Engine) tasksResponse(requestID string) map[string]any {
	_, tasks := e.store.Snapshot()
	resp := map[string]any{"type": "tasks", "tasks": tasks}
	if requestID != "" {
		resp["requestId"] = requestID
	}
	return resp
}

func (e *Engine) settingsResponse(requestID string) map[string]any {
	settings, _ := e.store.Snapshot()
	resp := map[string]any{"type": "settings", "settings": settings}
	if requestID != "" {
		resp["requestId"] = requestID
	}
	return resp
}

func (e *Engine) clearSignal(id string) {
	e.mu.Lock()
	delete(e.signaled, id)
	e.mu.Unlock()
}

// preserveCompletedTask 防止桌面端自动保存在 listTasks 返回前用旧快照把已完成任务写回进行中。
func preserveCompletedTask(incoming *task.Task, store *config.Store) {
	if incoming == nil || incoming.ID == "" {
		return
	}
	_, tasks := store.Snapshot()
	for _, ex := range tasks {
		if ex.ID != incoming.ID || !ex.IsCompleted() {
			continue
		}
		incoming.Status = task.StatusCompleted
		incoming.Completed = true
		if ex.CompletedAt != nil {
			at := *ex.CompletedAt
			incoming.CompletedAt = &at
		}
		return
	}
}
