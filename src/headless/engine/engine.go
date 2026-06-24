// Package engine 串联配置、任务计算与 IPC 命令处理，是 Headless 的业务核心。
package engine

import (
	"encoding/json"
	"log/slog"
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
	_, tasks := e.store.Snapshot()
	layout := task.BuildLayout(tasks, now)

	e.mu.Lock()
	paused, hidden := e.paused, e.hidden
	events := e.collectExpiredLocked(tasks, now)
	e.mu.Unlock()

	st := ipc.State{
		Version:  1,
		Segments: layout.Segments,
		Expired:  events,
	}
	switch {
	case paused:
		st.State = "paused"
		st.Visible = false
	case !layout.HasActive && layout.HadAny:
		st.State = "expired"
		st.Visible = false
	case !layout.HasActive:
		st.State = "idle"
		st.Visible = false
	default:
		st.State = "running"
		st.Visible = !hidden
	}
	if layout.HasActive {
		st.TimelineStart = layout.TimelineStart.Format(time.RFC3339)
		st.TimelineEnd = layout.TimelineEnd.Format(time.RFC3339)
	}
	return st
}

// collectExpiredLocked 返回自上次以来新到期的任务事件（一次性）。调用方需持有锁。
func (e *Engine) collectExpiredLocked(tasks []*task.Task, now time.Time) []ipc.ExpiredEvent {
	settings, _ := e.store.Snapshot()
	var out []ipc.ExpiredEvent
	for _, t := range tasks {
		if t.IsExpired(now) && !e.signaled[t.ID] {
			e.signaled[t.ID] = true
			out = append(out, ipc.ExpiredEvent{
				TaskID:   t.ID,
				Name:     t.Name,
				Behavior: string(settings.ExpiredBehavior),
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
	case "listTasks":
		_, tasks := e.store.Snapshot()
		return map[string]any{"type": "tasks", "tasks": tasks}
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
