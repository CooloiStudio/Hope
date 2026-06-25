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

// 到期提醒行为（多选，自动互斥；全局为默认值，任务可单独覆盖）。
const (
	BehaviorKeep   = "keep"   // 保持显示：到期后该任务保留为满色段
	BehaviorBlink  = "blink"  // 闪烁提醒：柔和 alpha 渐变，持续到用户查看
	BehaviorNotify = "notify" // 系统通知：到期时一次性气球提示
	BehaviorHide   = "hide"   // 自动隐藏：到期后移除该段
)

// ContainsBehavior 报告行为集合是否含指定项。
func ContainsBehavior(bs []string, name string) bool {
	for _, b := range bs {
		if b == name {
			return true
		}
	}
	return false
}

// KeepsVisibleWhenExpired 报告到期任务是否仍应保留在顶栏。
// 仅当显式 hide 且未叠加 keep/blink 时才隐藏；其余（含空集合）默认保留。
func KeepsVisibleWhenExpired(bs []string) bool {
	if ContainsBehavior(bs, BehaviorHide) &&
		!ContainsBehavior(bs, BehaviorKeep) &&
		!ContainsBehavior(bs, BehaviorBlink) {
		return false
	}
	return true
}

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
	// ExpiredBehaviors 为任务级到期提醒覆盖；为空表示沿用全局默认。
	ExpiredBehaviors []string `json:"expiredBehaviors,omitempty"`
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
	TaskID    string    `json:"taskId"`
	Name      string    `json:"name"`
	Color     string    `json:"color"`
	Gif       string    `json:"gif,omitempty"`
	BarStart  float64   `json:"barStart"`
	BarEnd    float64   `json:"barEnd"`
	Percent   float64   `json:"percent"`
	FillEnd   float64   `json:"fillEnd"`
	EndAt     time.Time `json:"endAt"`              // 供 Overlay 悬停计算倒计时（§5.4 修改 1）
	Expired   bool      `json:"expired,omitempty"`  // 该段对应已到期但保留显示的任务
	Behaviors []string  `json:"behaviors,omitempty"` // 该任务生效的到期提醒（供 Overlay 闪烁判定）
}

// Layout 为顶栏的整体布局结果。
type Layout struct {
	Segments  []Segment
	HasActive bool // 是否存在未过期任务
	HadAny    bool // 是否存在任意任务（用于区分 idle / expired）
}

// BuildLayout 依据任务构建顶栏分段（模型 v2，文档 §7.2）。
//
// 规则：
//   - 未过期任务按各任务 percent 升序排列；到期且生效行为保留显示的任务按 100% 计入末端。
//   - 顶栏代表 0–100% 进度刻度：第 i 段占据 [p_{i-1}, p_i]（p_0=0），满涂任务 i 的颜色。
//   - 最大 percent 之后的区间保持透明（不生成色段）。
//   - percent 相同导致的零宽段直接跳过（不绘制）。
//
// behaviorsOf 返回某任务生效的到期提醒集合（任务级覆盖回退全局）；用于判定到期任务是否保留及标记闪烁。
func BuildLayout(tasks []*Task, now time.Time, behaviorsOf func(*Task) []string) Layout {
	var out Layout
	out.HadAny = len(tasks) > 0

	type item struct {
		t         *Task
		pct       float64
		expired   bool
		behaviors []string
	}
	items := make([]item, 0, len(tasks))
	for _, t := range tasks {
		bs := behaviorsOf(t)
		if t.IsExpired(now) {
			if KeepsVisibleWhenExpired(bs) {
				items = append(items, item{t, 100, true, bs})
			}
			continue
		}
		items = append(items, item{t, t.Percent(now), false, bs})
		out.HasActive = true
	}
	if len(items) == 0 {
		return out
	}

	sort.SliceStable(items, func(i, j int) bool {
		return items[i].pct < items[j].pct
	})

	prev := 0.0
	for _, e := range items {
		p := e.pct
		if p <= prev {
			continue // 零宽（或重复 percent）色段不绘制
		}
		out.Segments = append(out.Segments, Segment{
			TaskID:    e.t.ID,
			Name:      e.t.Name,
			Color:     e.t.Color,
			Gif:       e.t.Gif,
			BarStart:  round1(prev),
			BarEnd:    round1(p),
			Percent:   round1(p),
			FillEnd:   round1(p), // 整段满涂；与 barEnd 一致，供 Overlay 绘制与 GIF 定位
			EndAt:     e.t.EndAt,
			Expired:   e.expired,
			Behaviors: e.behaviors,
		})
		prev = p
	}
	return out
}

func round1(v float64) float64 {
	return float64(int64(v*10+0.5)) / 10
}
