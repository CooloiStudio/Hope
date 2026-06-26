// Package engine 串联配置、任务计算与 IPC 命令处理，是 Headless 的业务核心。
package engine

import (
	"encoding/json"
	"log/slog"
	"math"
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
	switch settings.BarPosition {
	case "allFour":
		segments = buildAllFourLayout(tasks, now, behaviorsOf, settings.BarDirection)
	default:
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

// buildAllFourLayout 将单条 0-100% 布局按顺时针 / 逆时针映射到屏幕四边。
func buildAllFourLayout(tasks []*task.Task, now time.Time, behaviorsOf func(*task.Task) []string, direction string) []task.Segment {
	layout := task.BuildLayout(tasks, now, behaviorsOf, "allFour")
	if len(layout.Segments) == 0 {
		return nil
	}

	sides := []string{"top", "right", "bottom", "left"}
	if direction == "counterClockwise" {
		sides = []string{"top", "left", "bottom", "right"}
	}

	const sideSpan = 25.0
	var out []task.Segment
	for _, seg := range layout.Segments {
		for i, side := range sides {
			sideStart := float64(i) * sideSpan
			sideEnd := sideStart + sideSpan

			is := math.Max(seg.BarStart, sideStart)
			ie := math.Min(seg.BarEnd, sideEnd)
			if ie <= is {
				continue
			}

			localStart := (is - sideStart) / sideSpan * 100.0
			localEnd := (ie - sideStart) / sideSpan * 100.0
			localFill := localEnd
			if seg.FillEnd < sideStart {
				localFill = localStart
			} else if seg.FillEnd < sideEnd {
				localFill = (seg.FillEnd - sideStart) / sideSpan * 100.0
			}

			out = append(out, task.Segment{
				TaskID:       seg.TaskID,
				Name:         seg.Name,
				Color:        seg.Color,
				Gif:          seg.Gif,
				ImageMaxSize: seg.ImageMaxSize,
				BarStart:     round1(localStart),
				BarEnd:       round1(localEnd),
				Percent:      round1(localEnd),
				FillEnd:      round1(localFill),
				EndAt:        seg.EndAt,
				Expired:      seg.Expired,
				Behaviors:    seg.Behaviors,
				Position:     side,
			})
		}
	}
	return out
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
