// Package engine 串联配置、任务计算与 IPC 命令处理，是 Headless 的业务核心。
package engine

import (
	"encoding/json"
	"fmt"
	"log/slog"
	"math"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"hope/headless/config"
	"hope/headless/ipc"
	"hope/headless/task"
)

// Engine 持有运行期状态（暂停 / 隐藏 / 到期信号）。
type Engine struct {
	store *config.Store
	log   *slog.Logger

	mu       sync.Mutex
	paused   bool
	hidden   bool
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

	e.mu.Lock()
	paused, hidden := e.paused, e.hidden
	events := e.collectExpiredLocked(tasks, now, behaviorsOf)
	e.mu.Unlock()

	st := ipc.State{
		Version:  1,
		Segments: segments,
		Expired:  events,
	}

	hadAny, hasActive := computeActivity(tasks, now)
	switch {
	case paused:
		st.State = "paused"
		st.Visible = false
	case len(segments) > 0:
		// 含未过期段或「保持显示」的到期段时顶栏可见。
		st.State = "running"
		st.Visible = !hidden
	case hadAny:
		st.State = "expired"
		st.Visible = false
	default:
		st.State = "idle"
		st.Visible = false
	}

	// 任意未过期任务存在时保持 running（即使当前没有可见段，如全被 hide）。
	if hasActive && !paused {
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

// computeActivity 返回是否存在未手动完成的任务，以及是否存在未过期任务。
func computeActivity(tasks []*task.Task, now time.Time) (hadAny, hasActive bool) {
	for _, t := range tasks {
		if t.Completed {
			continue
		}
		hadAny = true
		if !t.IsExpired(now) {
			hasActive = true
		}
	}
	return
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
		if t.Completed {
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

	var out []task.Segment

	for _, t := range tasks {
		if t.Completed {
			continue
		}
		bs := behaviorsOf(t)
		if !t.IsExpired(now) && t.Percent(now) <= 0 {
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
			pct = t.Percent(now)
		}

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
				ImageRotation: rotationForSide(sides, i),
			}
			if isActive {
				seg.Gif = t.Gif
			}
			out = append(out, seg)
		}
	}
	// 调试日志：写入 %APPDATA%\Hope\logs\allfour-debug.log 并输出到 stderr
	// 仅在四边环绕模式下调用，因此日志只在勾选"我全都要"时输出。
	writeAllFourDebugLog(w, h, sides, cum, perim, tasks, now, out)

	return out
}

// writeAllFourDebugLog 将四边环绕的关键计算值写入调试日志文件并输出到 stderr。
// 仅当四边环绕模式启用时由 buildAllFourLayout 调用，因此日志只在勾选"我全都要"时输出。
// 文件位于 %APPDATA%\Hope\logs\allfour-debug.log，每次调用覆盖写入最新状态。
func writeAllFourDebugLog(screenW, screenH float64, sides []string, cum []float64, perim float64, tasks []*task.Task, now time.Time, out []task.Segment) {
	dir := os.Getenv("APPDATA")
	if dir == "" {
		dir = os.TempDir()
	}
	logDir := filepath.Join(dir, "Hope", "logs")
	os.MkdirAll(logDir, 0755)
	logPath := filepath.Join(logDir, "allfour-debug.log")

	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("t=%s  screen=%.0f x %.0f  perim=%.0f\n",
		now.Format("15:04:05.000"), screenW, screenH, perim))
	sb.WriteString(fmt.Sprintf("sides=%v\n", sides))
	sb.WriteString(fmt.Sprintf("cum  =%v\n", cum))

	for _, t := range tasks {
		if t.Completed {
			continue
		}
		pct := t.Percent(now)
		x := perim * pct / 100.0
		sb.WriteString(fmt.Sprintf("\n  task=%-20s  pct=%.2f%%  x=%.2f\n", t.Name, pct, x))
	}

	if len(out) == 0 {
		sb.WriteString("\n  [无 Segment 生成]\n")
	}
	for i, seg := range out {
		sb.WriteString(fmt.Sprintf("  seg[%d] pos=%-6s  barEnd=%.2f%%  dir=%-8s  rot=%.1f°  gif=%v\n",
			i, seg.Position, seg.BarEnd, seg.Direction, seg.ImageRotation, seg.Gif != ""))
	}

	content := sb.String()
	os.WriteFile(logPath, []byte(content), 0644)
	fmt.Fprint(os.Stderr, content)
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

// rotationForSide 返回 sides[idx] 对应的图片旋转角度（度）。
// 所有边均保持图片水平、顶部朝上，让图片自然贴附于进度条旁。
// 此映射只取决于目标边的位置，与环绕方向无关。
func rotationForSide(sides []string, idx int) float64 {
	return 0
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
			if cmd.Task.CreatedAt.IsZero() {
				cmd.Task.CreatedAt = time.Now()
			}
			e.store.UpsertTask(cmd.Task)
			e.clearSignal(cmd.Task.ID)
		}
	case "deleteTask":
		if cmd.TaskID != "" {
			e.store.DeleteTask(cmd.TaskID)
			e.clearSignal(cmd.TaskID)
		}
	case "completeTask":
		if cmd.TaskID != "" {
			_, tasks := e.store.Snapshot()
			for _, t := range tasks {
				if t.ID == cmd.TaskID {
					now := time.Now()
					advanced := t.AdvanceIfRecurring(now)
					if !advanced {
						// 非循环任务：标记为完成，不再渲染。
						t.Completed = true
						completedAt := now
						t.CompletedAt = &completedAt
					}
					e.store.UpsertTask(t)
					e.clearSignal(cmd.TaskID)
					break
				}
			}
		}
	case "listTasks":
		_, tasks := e.store.Snapshot()
		return map[string]any{"type": "tasks", "tasks": tasks}
	case "getSettings":
		settings, _ := e.store.Snapshot()
		return map[string]any{"type": "settings", "settings": settings}
	case "updateSettings":
		if len(cmd.Settings) > 0 {
			var ns config.Settings
			if err := json.Unmarshal(cmd.Settings, &ns); err == nil {
				e.store.UpdateSettings(ns)
			}
		}
	case "pause":
		e.setPaused(true)
	case "resume":
		e.setPaused(false)
	case "hide":
		e.setHidden(true)
	case "show":
		e.setHidden(false)
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

func (e *Engine) setPaused(v bool) { e.mu.Lock(); e.paused = v; e.mu.Unlock() }
func (e *Engine) setHidden(v bool) { e.mu.Lock(); e.hidden = v; e.mu.Unlock() }

func (e *Engine) clearSignal(id string) {
	e.mu.Lock()
	delete(e.signaled, id)
	e.mu.Unlock()
}
