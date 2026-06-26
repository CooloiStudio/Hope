// Package config 负责 Hope 的本地配置与任务列表持久化。
// 数据落盘于 %APPDATA%\Hope。
package config

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"sync"

	"hope/headless/task"
)

// utf8BOM 为 UTF-8 字节顺序标记；记事本等工具可能写入，需在解析前剥离。
var utf8BOM = []byte{0xEF, 0xBB, 0xBF}

func stripBOM(b []byte) []byte { return bytes.TrimPrefix(b, utf8BOM) }

// Settings 为用户可配置项，对应文档 §7.4。
// ExpiredBehaviors 为「全局默认」到期提醒（多选）；任务可在 task.Task.ExpiredBehaviors 单独覆盖。
type Settings struct {
	BarHeightPx         int      `json:"barHeightPx"`
	ExpiredBehaviors    []string `json:"expiredBehaviors"`
	RefreshSec          int      `json:"refreshSec"`
	Monitor             string   `json:"monitor"` // 首版仅 "primary"
	Autostart           bool     `json:"autostart"`
	ShowConfigAtRuntime bool     `json:"showConfigAtRuntime"`
	Language            string   `json:"language"`
	// BarPosition 全局进度条位置：top / bottom / left / right / allFour（默认 top）。
	BarPosition string `json:"barPosition"`
	// BarDirection 全局进度条方向：forward / reverse / clockwise / counterClockwise。
	// 在 top/bottom 时默认 forward；在 left/right 时默认 forward；在 allFour 时默认 clockwise。
	BarDirection string `json:"barDirection"`
	// AdvancedPosition 为 true 时允许为单个任务指定展示位置（在高级设置中开启）。
	AdvancedPosition bool `json:"advancedPosition"`
	// ScreenWidth 主屏幕工作区宽度（像素），四边模式下用于计算物理周长。
	ScreenWidth float64 `json:"screenWidth"`
	// ScreenHeight 主屏幕工作区高度（像素），四边模式下用于计算物理周长。
	ScreenHeight float64 `json:"screenHeight"`
	// LegacyExpiredBehavior 兼容旧版单值字段，仅用于迁移读入，迁移后清空不再写出。
	LegacyExpiredBehavior string `json:"expiredBehavior,omitempty"`
}

// DefaultSettings 返回文档约定的默认设置。
func DefaultSettings() Settings {
	return Settings{
		BarHeightPx:         4,
		ExpiredBehaviors:    []string{task.BehaviorKeep},
		RefreshSec:          1,
		Monitor:             "primary",
		Autostart:           false,
		ShowConfigAtRuntime: false,
		Language:            "zh-CN",
		BarPosition:         "top",
		BarDirection:        "forward",
		AdvancedPosition:    false,
		ScreenWidth:         0,
		ScreenHeight:        0,
	}
}

// Store 聚合配置与任务，并提供线程安全的持久化。
type Store struct {
	mu       sync.RWMutex
	dir      string
	Settings Settings     `json:"-"`
	Tasks    []*task.Task `json:"-"`
}

type settingsFile struct {
	Settings Settings `json:"settings"`
}

type tasksFile struct {
	Tasks []*task.Task `json:"tasks"`
}

// Dir 返回 Hope 的数据目录（%APPDATA%\Hope）。
func Dir() string {
	base := os.Getenv("APPDATA")
	if base == "" {
		base, _ = os.UserConfigDir()
	}
	return filepath.Join(base, "Hope")
}

// Load 从磁盘读取配置与任务；文件缺失时返回默认值。
func Load() (*Store, error) {
	dir := Dir()
	if err := os.MkdirAll(filepath.Join(dir, "logs"), 0o755); err != nil {
		return nil, err
	}
	s := &Store{dir: dir, Settings: DefaultSettings()}

	if b, err := os.ReadFile(filepath.Join(dir, "config.json")); err == nil {
		var sf settingsFile
		if json.Unmarshal(stripBOM(b), &sf) == nil {
			s.Settings = mergeSettings(DefaultSettings(), sf.Settings)
		}
	}
	if b, err := os.ReadFile(filepath.Join(dir, "tasks.json")); err == nil {
		var tf tasksFile
		if json.Unmarshal(stripBOM(b), &tf) == nil {
			s.Tasks = tf.Tasks
		}
	}
	return s, nil
}

// mergeSettings 用已加载值覆盖默认值中的非零字段，保证向后兼容。
func mergeSettings(def, loaded Settings) Settings {
	out := def
	if loaded.BarHeightPx > 0 {
		out.BarHeightPx = loaded.BarHeightPx
	}
	switch {
	case len(loaded.ExpiredBehaviors) > 0:
		out.ExpiredBehaviors = loaded.ExpiredBehaviors
	case loaded.LegacyExpiredBehavior != "":
		// 旧版单值 → 迁移为多选集合
		out.ExpiredBehaviors = []string{loaded.LegacyExpiredBehavior}
	}
	if loaded.RefreshSec > 0 {
		out.RefreshSec = loaded.RefreshSec
	}
	if loaded.Monitor != "" {
		out.Monitor = loaded.Monitor
	}
	if loaded.Language != "" {
		out.Language = loaded.Language
	}
	if loaded.BarPosition != "" {
		out.BarPosition = loaded.BarPosition
	}
	if loaded.BarDirection != "" {
		out.BarDirection = loaded.BarDirection
	}
	out.Autostart = loaded.Autostart
	out.ShowConfigAtRuntime = loaded.ShowConfigAtRuntime
	out.AdvancedPosition = loaded.AdvancedPosition
	if loaded.ScreenWidth > 0 {
		out.ScreenWidth = loaded.ScreenWidth
	}
	if loaded.ScreenHeight > 0 {
		out.ScreenHeight = loaded.ScreenHeight
	}
	return out
}

// SaveSettings 持久化设置。
func (s *Store) SaveSettings() error {
	s.mu.RLock()
	defer s.mu.RUnlock()
	b, err := json.MarshalIndent(settingsFile{Settings: s.Settings}, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(s.dir, "config.json"), b, 0o644)
}

// SaveTasks 持久化任务列表。
func (s *Store) SaveTasks() error {
	s.mu.RLock()
	defer s.mu.RUnlock()
	b, err := json.MarshalIndent(tasksFile{Tasks: s.Tasks}, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(s.dir, "tasks.json"), b, 0o644)
}

// Snapshot 返回当前任务的浅拷贝切片，供计算使用。
func (s *Store) Snapshot() (Settings, []*task.Task) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	cp := make([]*task.Task, len(s.Tasks))
	copy(cp, s.Tasks)
	return s.Settings, cp
}

// UpsertTask 新增或按 ID 更新任务。
func (s *Store) UpsertTask(t *task.Task) {
	s.mu.Lock()
	for i, ex := range s.Tasks {
		if ex.ID == t.ID {
			s.Tasks[i] = t
			s.mu.Unlock()
			_ = s.SaveTasks()
			return
		}
	}
	s.Tasks = append(s.Tasks, t)
	s.mu.Unlock()
	_ = s.SaveTasks()
}

// DeleteTask 按 ID 删除任务。
func (s *Store) DeleteTask(id string) {
	s.mu.Lock()
	out := s.Tasks[:0]
	for _, t := range s.Tasks {
		if t.ID != id {
			out = append(out, t)
		}
	}
	s.Tasks = out
	s.mu.Unlock()
	_ = s.SaveTasks()
}

// UpdateSettings 覆盖设置并持久化。
func (s *Store) UpdateSettings(ns Settings) {
	s.mu.Lock()
	s.Settings = mergeSettings(s.Settings, ns)
	s.mu.Unlock()
	_ = s.SaveSettings()
}
