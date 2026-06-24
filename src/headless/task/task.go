// Package task 定义任务模型与基于墙钟的进度 / 分段计算。
// 所有进度一律由 time.Now() 实时推导，不累加、不因休眠或暂停冻结（文档 §5.1 / §7.2）。
package task

import (
	"sort"
	"time"
)

// Type 任务类型。
type Type string

const (
	Scheduled Type = "scheduled" // 定时任务：用户填写 startAt + endAt
	Instant   Type = "instant"   // 即时任务：仅 endAt，startAt 取 createdAt
)

// Task 为单个任务。
type Task struct {
	ID        string     `json:"id"`
	Name      string     `json:"name"`
	Type      Type       `json:"type"`
	Color     string     `json:"color"`         // #RRGGBB
	Gif       string     `json:"gif,omitempty"` // 可选：本地 GIF 路径，挂在进度条下方跟随进度前沿播放
	StartAt   *time.Time `json:"startAt,omitempty"`
	EndAt     time.Time  `json:"endAt"`
	CreatedAt time.Time  `json:"createdAt"`
}

// EffectiveStart 返回用于计算的有效开始时刻。
func (t *Task) EffectiveStart() time.Time {
	if t.Type == Scheduled && t.StartAt != nil {
		return *t.StartAt
	}
	return t.CreatedAt
}

// Percent 返回任务自身进度（0~100），按墙钟实时计算。
func (t *Task) Percent(now time.Time) float64 {
	start := t.EffectiveStart()
	total := t.EndAt.Sub(start)
	if total <= 0 {
		return 100
	}
	p := float64(now.Sub(start)) / float64(total) * 100
	return clamp(p, 0, 100)
}

// IsExpired 当前时刻是否已过截止。
func (t *Task) IsExpired(now time.Time) bool {
	return !now.Before(t.EndAt)
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

// Segment 为顶栏上的一个色段，对应 IPC 广播结构（文档 §5.2）。
type Segment struct {
	TaskID   string    `json:"taskId"`
	Name     string    `json:"name"`
	Color    string    `json:"color"`
	Gif      string    `json:"gif,omitempty"`
	BarStart float64   `json:"barStart"`
	BarEnd   float64   `json:"barEnd"`
	Percent  float64   `json:"percent"`
	FillEnd  float64   `json:"fillEnd"`
	EndAt    time.Time `json:"endAt"` // 供 Overlay 悬停计算倒计时（§5.4 修改 1）
}

// Layout 为顶栏的整体布局结果。
type Layout struct {
	Segments  []Segment
	HasActive bool // 是否存在未过期任务
	HadAny    bool // 是否存在任意任务（用于区分 idle / expired）
}

// BuildLayout 依据未过期任务构建顶栏分段（模型 v2，文档 §7.2）。
//
// 规则：
//   - 仅纳入未过期任务；按各任务 percent 升序排列。
//   - 顶栏代表 0–100% 进度刻度：第 i 段占据 [p_{i-1}, p_i]（p_0=0），满涂任务 i 的颜色。
//   - 最大 percent 之后的区间保持透明（不生成色段）。
//   - percent 相同导致的零宽段直接跳过（不绘制）。
func BuildLayout(tasks []*Task, now time.Time) Layout {
	var out Layout
	out.HadAny = len(tasks) > 0

	type taskPct struct {
		t   *Task
		pct float64
	}
	active := make([]taskPct, 0, len(tasks))
	for _, t := range tasks {
		if !t.IsExpired(now) {
			active = append(active, taskPct{t, t.Percent(now)})
		}
	}
	if len(active) == 0 {
		return out
	}
	out.HasActive = true

	sort.SliceStable(active, func(i, j int) bool {
		return active[i].pct < active[j].pct
	})

	prev := 0.0
	for _, e := range active {
		p := e.pct
		if p <= prev {
			continue // 零宽（或重复 percent）色段不绘制
		}
		out.Segments = append(out.Segments, Segment{
			TaskID:   e.t.ID,
			Name:     e.t.Name,
			Color:    e.t.Color,
			Gif:      e.t.Gif,
			BarStart: round1(prev),
			BarEnd:   round1(p),
			Percent:  round1(p),
			FillEnd:  round1(p), // 整段满涂；与 barEnd 一致，供 Overlay 绘制与 GIF 定位
			EndAt:    e.t.EndAt,
		})
		prev = p
	}
	return out
}

func round1(v float64) float64 {
	return float64(int64(v*10+0.5)) / 10
}
