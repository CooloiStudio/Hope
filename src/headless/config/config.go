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

// boolPtr 返回指向给定布尔值的指针，用于带默认值的可选布尔设置。
func boolPtr(b bool) *bool { return &b }

// Settings 为用户可配置项，对应文档 §7.4。
// ExpiredBehaviors 为「全局默认」到期提醒（多选）；任务可在 task.Task.ExpiredBehaviors 单独覆盖。
type Settings struct {
	BarHeightPx         int      `json:"barHeightPx"`
	// ImageMaxHeightPx 为图片最大高度（px，全局统一）；段图片按此高度等比缩放。
	ImageMaxHeightPx    int      `json:"imageMaxHeightPx"`
	ExpiredBehaviors    []string `json:"expiredBehaviors"`
	RefreshSec          int      `json:"refreshSec"`
	Monitor             string   `json:"monitor"` // 首版仅 "primary"
	Autostart           bool     `json:"autostart"`
	ShowConfigAtRuntime bool     `json:"showConfigAtRuntime"`
	// ShowAdvancedSettings 为 true 时全局设置窗展开进度条高级选项。
	ShowAdvancedSettings bool `json:"showAdvancedSettings"`
	Language            string   `json:"language"`
	// BarPosition 全局进度条位置：top / bottom / left / right（默认 top）。
	BarPosition string `json:"barPosition"`
	// BarDirection 全局进度条方向：forward / reverse。
	// 在 top/bottom 时默认 forward（水平从左到右/从右到左）；在 left/right 时默认 forward（垂直从上到下/从下到上）。
	BarDirection string `json:"barDirection"`
	// BarDirections 各边前进方向（AdvancedPosition 开启时生效）：top/bottom/left/right → forward/reverse。
	BarDirections map[string]string `json:"barDirections,omitempty"`
	// AdvancedPosition 为 true 时允许为单个任务指定展示位置（在高级设置中开启）。
	AdvancedPosition bool `json:"advancedPosition"`
	// AllFour 为 true 时启用四边环绕（我全都要）。从 BarPosition 出发沿 BarDirection 方向环绕。
	AllFour bool `json:"allFour"`
	// AutoUpdate 是否自动下载更新（默认开）；关闭后仅检测并提示，不自动下载。
	// 用指针区分「旧配置未写该字段」(nil→采用默认开) 与「用户显式关闭」(指向 false)。
	AutoUpdate *bool `json:"autoUpdate,omitempty"`
	// AllowTelemetry 是否允许发送匿名软件活跃信息（默认开）；关闭后桌面端不再发送任何遥测。
	// 同样用指针区分「旧配置未写该字段」(nil→采用默认开) 与「用户显式关闭」(指向 false)。
	AllowTelemetry *bool `json:"allowTelemetry,omitempty"`
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
		ImageMaxHeightPx:    15,
		// 默认到期后自动显示（保持满色段），不附加额外效果；可叠加 blink/celebrate/notify。
		ExpiredBehaviors:    []string{},
		RefreshSec:          1,
		Monitor:             "primary",
		Autostart:           false,
		ShowConfigAtRuntime: false,
		Language:            "zh-CN",
		BarPosition:         "top",
		BarDirection:        "forward",
		BarDirections:       defaultBarDirections(),
		AdvancedPosition:    false,
		AllFour:             false,
		AutoUpdate:          boolPtr(true),
		AllowTelemetry:      boolPtr(true),
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
	if loaded.ImageMaxHeightPx > 0 {
		out.ImageMaxHeightPx = loaded.ImageMaxHeightPx
	}
	switch {
	case len(loaded.ExpiredBehaviors) > 0:
		out.ExpiredBehaviors = sanitizeBehaviors(loaded.ExpiredBehaviors)
	case loaded.LegacyExpiredBehavior != "":
		// 旧版单值 → 迁移为多选集合（keep/hide 已废弃，过滤掉）
		out.ExpiredBehaviors = sanitizeBehaviors([]string{loaded.LegacyExpiredBehavior})
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
	if len(loaded.BarDirections) > 0 {
		out.BarDirections = mergeBarDirections(out.BarDirections, loaded.BarDirections)
	}
	out.Autostart = loaded.Autostart
	out.ShowConfigAtRuntime = loaded.ShowConfigAtRuntime
	out.ShowAdvancedSettings = loaded.ShowAdvancedSettings
	out.AdvancedPosition = loaded.AdvancedPosition
	out.AllFour = loaded.AllFour
	if loaded.AutoUpdate != nil {
		out.AutoUpdate = loaded.AutoUpdate
	}
	if loaded.AllowTelemetry != nil {
		out.AllowTelemetry = loaded.AllowTelemetry
	}
	// 兼容旧版 BarPosition="allFour"：迁移为 top + AllFour=true
	// 必须在读取 loaded.AllFour 之后执行，以确保旧配置（无 AllFour 字段）能正确迁移。
	if out.BarPosition == "allFour" {
		out.BarPosition = "top"
		out.AllFour = true
	}
	if loaded.ScreenWidth > 0 {
		out.ScreenWidth = loaded.ScreenWidth
	}
	if loaded.ScreenHeight > 0 {
		out.ScreenHeight = loaded.ScreenHeight
	}
	return out
}

func defaultBarDirections() map[string]string {
	return map[string]string{
		"top": "forward", "bottom": "forward", "left": "forward", "right": "forward",
	}
}

func mergeBarDirections(base, loaded map[string]string) map[string]string {
	out := defaultBarDirections()
	for k, v := range base {
		if v != "" {
			out[k] = v
		}
	}
	for k, v := range loaded {
		if v == "forward" || v == "reverse" {
			out[k] = v
		}
	}
	return out
}

// DirectionFor 返回指定边的有效前进方向。
// AdvancedPosition 开启时用 BarDirections；否则仅全局 BarPosition 使用 BarDirection。
func (s Settings) DirectionFor(position string) string {
	if s.AdvancedPosition {
		if d, ok := s.BarDirections[position]; ok && d != "" {
			return d
		}
		return "forward"
	}
	if position == s.BarPosition && s.BarDirection != "" {
		return s.BarDirection
	}
	return "forward"
}

// sanitizeBehaviors 仅保留可叠加的有效到期提醒（blink/celebrate/notify），
// 过滤已废弃的 keep/hide（新模型默认即「自动显示」）。返回非 nil 切片（可能为空）。
func sanitizeBehaviors(bs []string) []string {
	out := make([]string, 0, len(bs))
	seen := map[string]bool{}
	for _, b := range bs {
		switch b {
		case task.BehaviorBlink, task.BehaviorCelebrate, task.BehaviorNotify:
			if !seen[b] {
				seen[b] = true
				out = append(out, b)
			}
		}
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
			merged := *t
			if merged.CreatedAt.IsZero() {
				merged.CreatedAt = ex.CreatedAt
			}
			s.Tasks[i] = &merged
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

// DeleteCompletedTasks 删除所有已完成任务，返回被删除任务的 ID 列表。
func (s *Store) DeleteCompletedTasks() []string {
	s.mu.Lock()
	var removed []string
	out := s.Tasks[:0]
	for _, t := range s.Tasks {
		if t.IsCompleted() {
			removed = append(removed, t.ID)
		} else {
			out = append(out, t)
		}
	}
	s.Tasks = out
	s.mu.Unlock()
	_ = s.SaveTasks()
	return removed
}

// UpdateSettings 覆盖设置并持久化。
// 约定：Desktop 的 updateSettings 携带完整用户意图，故以下字段允许「显式置空/默认」覆盖，
// 不再走「非零才覆盖」：ExpiredBehaviors（可清空＝无附加效果）、BarDirection（""＝自动）。
// 屏幕尺寸不由此命令维护（见 SetScreen），缺省（≤0）时保留现值，避免被默认值覆盖。
func (s *Store) UpdateSettings(ns Settings) {
	s.mu.Lock()
	cur := s.Settings
	merged := mergeSettings(cur, ns)
	merged.ExpiredBehaviors = sanitizeBehaviors(ns.ExpiredBehaviors)
	merged.BarDirection = ns.BarDirection
	if len(ns.BarDirections) > 0 {
		merged.BarDirections = mergeBarDirections(merged.BarDirections, ns.BarDirections)
	}
	if ns.ScreenWidth <= 0 {
		merged.ScreenWidth = cur.ScreenWidth
	}
	if ns.ScreenHeight <= 0 {
		merged.ScreenHeight = cur.ScreenHeight
	}
	s.Settings = merged
	s.mu.Unlock()
	_ = s.SaveSettings()
}

// SetScreen 仅更新主屏幕工作区尺寸并持久化，不触碰其它设置。
// Desktop 启动/连接时上报屏幕尺寸走此路径，避免误用默认值覆盖用户设置。
func (s *Store) SetScreen(w, h float64) {
	s.mu.Lock()
	if w > 0 {
		s.Settings.ScreenWidth = w
	}
	if h > 0 {
		s.Settings.ScreenHeight = h
	}
	s.mu.Unlock()
	_ = s.SaveSettings()
}
