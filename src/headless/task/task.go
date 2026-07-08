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

// 到期提醒行为（可叠加；全局为默认值，任务可单独覆盖）。
// 新模型：到期默认「自动显示」（保留满色段），无需显式选项；下列为可叠加附加效果。
// keep/hide 已废弃（保留常量仅用于兼容旧数据解析），不再作为有效行为。
const (
	BehaviorBlink     = "blink"     // 闪烁提醒：柔和 alpha 渐变，持续到用户查看
	BehaviorNotify    = "notify"    // 系统通知：到期时一次性气球提示
	BehaviorCelebrate = "celebrate" // 全屏庆祝：四边同步闪烁

	// 已废弃，仅兼容旧配置/任务数据。
	BehaviorKeep = "keep"
	BehaviorHide = "hide"
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
// 新模型：到期默认自动显示，恒为 true（已移除「自动隐藏」选项）。
func KeepsVisibleWhenExpired(bs []string) bool {
	return true
}

// Status 任务生命周期状态。使用字符串枚举，便于后续扩展新状态。
type Status string

const (
	StatusActive    Status = "active"    // 进行中：正常参与渲染
	StatusCompleted Status = "completed" // 已完成：仅由用户手动「完成」置入，不再渲染
)

// RecurMode 循环模式（仅定时任务可用，文档 §7.5）。
type RecurMode string

const (
	RecurNone   RecurMode = ""       // 不循环：单次任务（默认）
	RecurDaily  RecurMode = "daily"  // 每天执行
	RecurEveryN RecurMode = "everyN" // 每 Interval 天执行
	RecurWeekly RecurMode = "weekly" // 按星期多选执行
)

// Recurrence 描述循环规则。循环任务每期由 startTs/endTs 定义绝对时间窗；
// 用户点「完成」后下一期起止戳整体累加 n×86400（daily=1、everyN=interval、weekly=7）。
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
	ImageMaxSize int     `json:"imageMaxSize,omitempty"` // 图片最大高度（px），默认 15，范围 15~30
	// StartTs / EndTs 为任务有效窗口的 Unix 秒（业务逻辑唯一依据）。
	StartTs int64 `json:"startTs,omitempty"`
	EndTs   int64 `json:"endTs,omitempty"`
	// StartAt / EndAt 仅用于读取旧版数据；落盘时不再写出。
	StartAt   *time.Time `json:"startAt,omitempty"`
	EndAt     time.Time  `json:"endAt,omitempty"`
	CreatedAt time.Time  `json:"createdAt"`
	// Status 任务生命周期状态；空字符串视为「进行中」（向后兼容旧数据）。
	Status Status `json:"status,omitempty"`
	// Completed 为旧版完成标记，仅用于兼容历史数据解析（见 IsCompleted）。
	Completed bool `json:"completed,omitempty"`
	// CompletedAt 记录用户手动完成的时间（可选）。
	CompletedAt *time.Time `json:"completedAt,omitempty"`
	// Position 为任务级展示位置覆盖；空字符串表示沿用全局设置。
	// 可选值：top / bottom / left / right。
	Position string `json:"position,omitempty"`
	// ExpiredBehaviors 为任务级到期提醒覆盖；为空表示沿用全局默认。
	ExpiredBehaviors []string `json:"expiredBehaviors,omitempty"`
	// Recurrence 为循环规则；nil / Mode 为空表示单次任务。
	Recurrence *Recurrence `json:"recurrence,omitempty"`
}

// IsCompleted 报告任务是否被用户标记为已完成。
// 以 Status 为准；Status 为空时回退到旧版 Completed 布尔字段，保证兼容历史数据。
func (t *Task) IsCompleted() bool {
	if t.Status != "" {
		return t.Status == StatusCompleted
	}
	return t.Completed
}

// Clone 返回任务的深拷贝（所有指针字段独立），用于循环任务完成时生成下一期副本。
func (t *Task) Clone() *Task {
	cp := *t
	if t.StartAt != nil {
		s := *t.StartAt
		cp.StartAt = &s
	}
	if t.CompletedAt != nil {
		c := *t.CompletedAt
		cp.CompletedAt = &c
	}
	if t.Recurrence != nil {
		r := *t.Recurrence
		if t.Recurrence.Weekdays != nil {
			r.Weekdays = append([]int(nil), t.Recurrence.Weekdays...)
		}
		cp.Recurrence = &r
	}
	if t.ExpiredBehaviors != nil {
		cp.ExpiredBehaviors = append([]string(nil), t.ExpiredBehaviors...)
	}
	return &cp
}

// EffectiveStart 返回用于展示/IPC 的有效开始时刻（由 startTs 派生）。
func (t *Task) EffectiveStart() time.Time {
	loc := t.timeLocation()
	return time.Unix(t.effectiveStartTs(), 0).In(loc)
}

// IsRecurring 报告任务是否为循环任务（仅定时 + 有开始时间戳 + 指定了循环模式）。
func (t *Task) IsRecurring() bool {
	return t.Type == Scheduled && t.effectiveStartTs() > 0 &&
		t.Recurrence != nil && t.Recurrence.Mode != RecurNone
}

// windowAt 由存储的 startTs/endTs 与 now 比较，返回时间窗及是否已开始（nowTs ≥ startTs）。
func (t *Task) windowAt(now time.Time) (start, end time.Time, started bool) {
	startTs := t.effectiveStartTs()
	endTs := t.effectiveEndTs()
	loc := t.timeLocation()
	start = time.Unix(startTs, 0).In(loc)
	end = time.Unix(endTs, 0).In(loc)
	nowTs := now.Unix()
	if nowTs < startTs {
		return start, end, false
	}
	return start, end, true
}

// minRenderPercent 窗口已开始但墙钟进度四舍五入为 0 时，仍生成可见色段的最小百分比。
const minRenderPercent = 0.1

// NextBoundaryAfter 返回 strictly 在 now 之后、本任务渲染状态可能变化的最近时刻（窗口开始或截止）。
// 已完成任务或无后续边界时返回零值。
func (t *Task) NextBoundaryAfter(now time.Time) time.Time {
	if t.IsCompleted() {
		return time.Time{}
	}
	startTs := t.effectiveStartTs()
	endTs := t.effectiveEndTs()
	nowTs := now.Unix()
	loc := t.timeLocation()
	if nowTs < startTs {
		return time.Unix(startTs, 0).In(loc)
	}
	if nowTs < endTs {
		return time.Unix(endTs, 0).In(loc)
	}
	return time.Time{}
}

// Percent 返回任务自身进度（0~100），按墙钟 Unix 秒实时计算。
func (t *Task) Percent(now time.Time) float64 {
	startTs := t.effectiveStartTs()
	endTs := t.effectiveEndTs()
	nowTs := now.Unix()
	if nowTs < startTs {
		return 0
	}
	total := endTs - startTs
	if total <= 0 {
		return 100
	}
	p := float64(nowTs-startTs) / float64(total) * 100
	return clamp(p, 0, 100)
}

// HasStarted 报告 now 是否已进入任务当前有效时间窗（含跨午夜循环的凌晨时段）。
func (t *Task) HasStarted(now time.Time) bool {
	_, _, started := t.windowAt(now)
	return started
}

// RenderPercent 返回用于顶栏绘制的进度；窗口已开始但计算值为 0 时抬升到 minRenderPercent，避免零宽段。
func (t *Task) RenderPercent(now time.Time) float64 {
	if t.IsExpired(now) {
		return 100
	}
	p := t.Percent(now)
	_, _, started := t.windowAt(now)
	if started && p <= 0 {
		return minRenderPercent
	}
	return p
}

// IsExpired 当前时刻是否已过截止（nowTs ≥ endTs 且已进入窗口）。
func (t *Task) IsExpired(now time.Time) bool {
	startTs := t.effectiveStartTs()
	endTs := t.effectiveEndTs()
	nowTs := now.Unix()
	if nowTs < startTs {
		return false
	}
	return nowTs >= endTs
}

// EffectiveEnd 返回当前窗口截止时刻（供 Segment.EndAt 倒计时显示）。
func (t *Task) EffectiveEnd(now time.Time) time.Time {
	loc := t.timeLocation()
	return time.Unix(t.effectiveEndTs(), 0).In(loc)
}

// AdvanceIfRecurring 将循环任务起止戳累加 n×86400，推进到下一期。
// 返回 true 表示已推进；返回 false 表示非循环任务。
func (t *Task) AdvanceIfRecurring(_ time.Time) bool {
	if !t.IsRecurring() {
		return false
	}
	step := t.RecurStepSeconds()
	if step <= 0 {
		return false
	}
	t.EnsureTimestamps()
	t.StartTs += step
	t.EndTs += step
	t.StartAt = nil
	t.EndAt = time.Time{}
	return true
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
	ImageMaxSize int    `json:"imageMaxSize,omitempty"`
	BarStart  float64   `json:"barStart"`
	BarEnd    float64   `json:"barEnd"`
	Percent   float64   `json:"percent"`
	FillEnd   float64   `json:"fillEnd"`
	EndAt     time.Time `json:"endAt"`              // 供 Overlay 悬停计算倒计时（§5.4 修改 1）
	Expired   bool      `json:"expired,omitempty"`  // 该段对应已到期但保留显示的任务
	Behaviors []string  `json:"behaviors,omitempty"` // 该任务生效的到期提醒（供 Overlay 闪烁判定）
	// Position 为该段应渲染到的位置；空字符串表示沿用全局设置。
	Position string `json:"position,omitempty"`
	// Direction 为该段在所属边上的填充方向（forward / reverse）；
	// 非四边环绕时为空，前端沿用窗口级 Direction。
	Direction string `json:"direction,omitempty"`
	// ImageRotation 为图片旋转角度（度），用于四边环绕时让图片始终从对应边向屏幕内侧。
	ImageRotation float64 `json:"imageRotation,omitempty"`
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
// position 为生成的段指定目标位置，供桌面端按边分组渲染。
func BuildLayout(tasks []*Task, now time.Time, behaviorsOf func(*Task) []string, position string) Layout {
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
		if t.IsCompleted() {
			continue // 用户手动完成的任务不渲染
		}
		bs := behaviorsOf(t)
		start, end, started := t.windowAt(now)
		if !started {
			continue // 预约任务未到开始时刻，不参与渲染
		}
		if !now.Before(end) { // 已过当前窗口截止
			if KeepsVisibleWhenExpired(bs) {
				items = append(items, item{t, 100, true, bs, end})
			}
			continue
		}
		pct := 0.0
		if total := end.Sub(start); total <= 0 {
			pct = 100
		} else {
			pct = t.RenderPercent(now)
		}
		out.HasActive = true
		items = append(items, item{t, pct, false, bs, end})
	}
	if len(items) == 0 {
		return out
	}

	// 拆分活跃 / 到期任务：
	//   活跃：按进度升序嵌套成相邻色带，第 i 段 [p_{i-1}, p_i]（低进度在前）。
	//   到期：均为 100%。若沿用嵌套，多个同为 100% 的到期段会因零宽被丢弃 → 只剩一种颜色，
	//         导致前端无法对多个完成任务做颜色轮换呼吸。改为各自铺满「剩余带」[maxActivePct, 100]
	//         （互相重叠），前端按 taskId 去重取色组成轮换序列。单个到期任务时与原行为一致。
	var active, expired []item
	for _, e := range items {
		if e.expired {
			expired = append(expired, e)
		} else {
			active = append(active, e)
		}
	}

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
			TaskID:    e.t.ID,
			Name:      e.t.Name,
			Color:     e.t.Color,
			Gif:       e.t.Gif,
			ImageMaxSize: e.t.ImageMaxSize,
			BarStart:  round1(prev),
			BarEnd:    round1(p),
			Percent:   round1(p),
			FillEnd:   round1(p), // 整段满涂；与 barEnd 一致，供 Overlay 绘制与 GIF 定位
			EndAt:     e.endAt,
			Expired:   false,
			Behaviors: e.behaviors,
			Position:  position,
		})
		prev = p
	}

	// 到期段按 ID 稳定排序后各自铺满 [prev, 100]（互相重叠），保证各边颜色顺序一致、可被前端去重取色。
	sort.SliceStable(expired, func(i, j int) bool { return expired[i].t.ID < expired[j].t.ID })
	for _, e := range expired {
		out.Segments = append(out.Segments, Segment{
			TaskID:    e.t.ID,
			Name:      e.t.Name,
			Color:     e.t.Color,
			Gif:       e.t.Gif,
			ImageMaxSize: e.t.ImageMaxSize,
			BarStart:  round1(prev),
			BarEnd:    100,
			Percent:   100,
			FillEnd:   100,
			EndAt:     e.endAt,
			Expired:   true,
			Behaviors: e.behaviors,
			Position:  position,
		})
	}
	return out
}

func round1(v float64) float64 {
	return float64(int64(v*10+0.5)) / 10
}
