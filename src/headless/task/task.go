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
	TaskID   string  `json:"taskId"`
	Name     string  `json:"name"`
	Color    string  `json:"color"`
	Gif      string  `json:"gif,omitempty"`
	BarStart float64 `json:"barStart"`
	BarEnd   float64 `json:"barEnd"`
	Percent  float64 `json:"percent"`
	FillEnd  float64 `json:"fillEnd"`
}

// Layout 为顶栏的整体布局结果。
type Layout struct {
	Segments      []Segment
	TimelineStart time.Time
	TimelineEnd   time.Time
	HasActive     bool // 是否存在未过期任务
	HadAny        bool // 是否存在任意任务（用于区分 idle / expired）
}

// BuildLayout 依据未过期任务构建顶栏分段。
//
// 规则（文档 §5.2）：
//   - 仅纳入未过期任务；按有效开始时间排序后从左到右拼接。
//   - 段宽按各任务时长占比分配；为保证顶栏连续无间隙，按时长归一化至 [0,100]。
//   - 段内自 barStart 向右填充至 fillEnd = barStart + width*percent/100。
func BuildLayout(tasks []*Task, now time.Time) Layout {
	var out Layout
	out.HadAny = len(tasks) > 0

	active := make([]*Task, 0, len(tasks))
	for _, t := range tasks {
		if !t.IsExpired(now) {
			active = append(active, t)
		}
	}
	if len(active) == 0 {
		return out
	}
	out.HasActive = true

	sort.SliceStable(active, func(i, j int) bool {
		si, sj := active[i].EffectiveStart(), active[j].EffectiveStart()
		if si.Equal(sj) {
			return active[i].EndAt.Before(active[j].EndAt)
		}
		return si.Before(sj)
	})

	out.TimelineStart = active[0].EffectiveStart()
	out.TimelineEnd = active[0].EndAt
	var totalDur float64
	durs := make([]float64, len(active))
	for i, t := range active {
		s := t.EffectiveStart()
		if s.Before(out.TimelineStart) {
			out.TimelineStart = s
		}
		if t.EndAt.After(out.TimelineEnd) {
			out.TimelineEnd = t.EndAt
		}
		d := t.EndAt.Sub(s).Seconds()
		if d <= 0 {
			d = 1
		}
		durs[i] = d
		totalDur += d
	}

	cursor := 0.0
	for i, t := range active {
		width := durs[i] / totalDur * 100
		barStart := cursor
		barEnd := cursor + width
		if i == len(active)-1 {
			barEnd = 100 // 末段对齐右边界，消除浮点误差
		}
		pct := t.Percent(now)
		fillEnd := barStart + (barEnd-barStart)*pct/100
		out.Segments = append(out.Segments, Segment{
			TaskID:   t.ID,
			Name:     t.Name,
			Color:    t.Color,
			Gif:      t.Gif,
			BarStart: round1(barStart),
			BarEnd:   round1(barEnd),
			Percent:  round1(pct),
			FillEnd:  round1(fillEnd),
		})
		cursor = barEnd
	}
	return out
}

func round1(v float64) float64 {
	return float64(int64(v*10+0.5)) / 10
}
