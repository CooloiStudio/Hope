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

// RecurMode 循环模式（仅定时任务可用，文档 §7.5）。
type RecurMode string

const (
	RecurNone   RecurMode = ""       // 不循环：单次任务（默认）
	RecurDaily  RecurMode = "daily"  // 每天执行
	RecurEveryN RecurMode = "everyN" // 每 Interval 天执行
	RecurWeekly RecurMode = "weekly" // 按星期多选执行
)

// Recurrence 描述循环规则。循环任务以 StartAt/EndAt 的「时分」定义每个发生日的时间窗，
// StartAt 的「日期」作为锚点（每 N 天计数 / 生效起始日）。支持跨午夜（截止时分 ≤ 开始时分 → 次日截止）。
type Recurrence struct {
	Mode     RecurMode `json:"mode,omitempty"`
	Interval int       `json:"interval,omitempty"` // everyN：≥1
	Weekdays []int     `json:"weekdays,omitempty"` // weekly：0=周日 … 6=周六
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
	// Recurrence 为循环规则；nil / Mode 为空表示单次任务。
	Recurrence *Recurrence `json:"recurrence,omitempty"`
}

// EffectiveStart 返回用于计算的有效开始时刻。
func (t *Task) EffectiveStart() time.Time {
	if t.Type == Scheduled && t.StartAt != nil {
		return *t.StartAt
	}
	return t.CreatedAt
}

// IsRecurring 报告任务是否为循环任务（仅定时 + 有开始时间 + 指定了循环模式）。
func (t *Task) IsRecurring() bool {
	return t.Type == Scheduled && t.StartAt != nil &&
		t.Recurrence != nil && t.Recurrence.Mode != RecurNone
}

func dateOf(t time.Time, loc *time.Location) time.Time {
	t = t.In(loc)
	return time.Date(t.Year(), t.Month(), t.Day(), 0, 0, 0, 0, loc)
}

func todOf(t time.Time) time.Duration {
	return time.Duration(t.Hour())*time.Hour +
		time.Duration(t.Minute())*time.Minute +
		time.Duration(t.Second())*time.Second
}

func isOccurrenceDay(rec *Recurrence, anchor, d time.Time) bool {
	if d.Before(anchor) {
		return false
	}
	switch rec.Mode {
	case RecurDaily:
		return true
	case RecurEveryN:
		n := rec.Interval
		if n < 1 {
			n = 1
		}
		days := int((d.Sub(anchor) + 12*time.Hour) / (24 * time.Hour)) // 四舍五入消除 DST 偏差
		return days%n == 0
	case RecurWeekly:
		for _, wd := range rec.Weekdays {
			if int(d.Weekday()) == wd {
				return true
			}
		}
		return false
	}
	return false
}

// windowAt 返回与 now 相关的有效时间窗 [start, end]。
//   - 非循环：恒返回任务固定的 [EffectiveStart, EndAt]，started=true。
//   - 循环：返回 start≤now 的最近一次发生窗口；若 now 早于首个发生窗口，started=false。
func (t *Task) windowAt(now time.Time) (start, end time.Time, started bool) {
	if !t.IsRecurring() {
		return t.EffectiveStart(), t.EndAt, true
	}
	loc := t.StartAt.Location()
	startTOD := todOf(*t.StartAt)
	endTOD := todOf(t.EndAt)
	anchor := dateOf(*t.StartAt, loc)
	rec := t.Recurrence

	maxBack := 8
	if rec.Mode == RecurEveryN && rec.Interval+1 > maxBack {
		maxBack = rec.Interval + 1
	}
	if maxBack > 400 {
		maxBack = 400
	}

	d0 := dateOf(now, loc)
	for i := 0; i <= maxBack; i++ {
		d := d0.AddDate(0, 0, -i)
		if d.Before(anchor) {
			break
		}
		if !isOccurrenceDay(rec, anchor, d) {
			continue
		}
		ws := d.Add(startTOD)
		we := d.Add(endTOD)
		if endTOD <= startTOD { // 跨午夜（或等长）→ 次日截止
			we = d.AddDate(0, 0, 1).Add(endTOD)
		}
		if !ws.After(now) { // ws ≤ now：最近一次发生窗口
			return ws, we, true
		}
	}
	return time.Time{}, time.Time{}, false
}

// Percent 返回任务自身进度（0~100），按墙钟实时计算（循环任务取当前发生窗口）。
func (t *Task) Percent(now time.Time) float64 {
	start, end, started := t.windowAt(now)
	if !started {
		return 0
	}
	total := end.Sub(start)
	if total <= 0 {
		return 100
	}
	p := float64(now.Sub(start)) / float64(total) * 100
	return clamp(p, 0, 100)
}

// IsExpired 当前时刻是否已过截止（循环任务指当前发生窗口已结束且未到下一次开始）。
func (t *Task) IsExpired(now time.Time) bool {
	_, end, started := t.windowAt(now)
	if !started {
		return false
	}
	return !now.Before(end)
}

// EffectiveEnd 返回与 now 相关窗口的截止时刻（供 Segment.EndAt 倒计时显示）。
func (t *Task) EffectiveEnd(now time.Time) time.Time {
	_, end, started := t.windowAt(now)
	if !started {
		return t.EndAt
	}
	return end
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
		endAt     time.Time
	}
	items := make([]item, 0, len(tasks))
	for _, t := range tasks {
		bs := behaviorsOf(t)
		start, end, started := t.windowAt(now)
		if started && !now.Before(end) { // 已过当前窗口截止
			if KeepsVisibleWhenExpired(bs) {
				items = append(items, item{t, 100, true, bs, end})
			}
			continue
		}
		pct := 0.0
		if started {
			if total := end.Sub(start); total <= 0 {
				pct = 100
			} else {
				pct = clamp(float64(now.Sub(start))/float64(total)*100, 0, 100)
			}
			out.HasActive = true
		}
		items = append(items, item{t, pct, false, bs, end})
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
			EndAt:     e.endAt,
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
