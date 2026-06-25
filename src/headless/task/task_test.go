package task

import (
	"testing"
	"time"
)

func mustTime(s string) time.Time {
	t, err := time.Parse(time.RFC3339, s)
	if err != nil {
		panic(err)
	}
	return t
}

// 测试用行为解析器。
func noBehaviors(*Task) []string   { return nil }
func hideBehaviors(*Task) []string { return []string{BehaviorHide} }
func keepBehaviors(*Task) []string { return []string{BehaviorKeep} }

// 验证文档示例：08:00–18:00 任务，17:00 时进度应为 90%。
func TestScheduledPercent90(t *testing.T) {
	start := mustTime("2026-06-23T08:00:00+08:00")
	task := &Task{
		ID:      "t1",
		Type:    Scheduled,
		StartAt: &start,
		EndAt:   mustTime("2026-06-23T18:00:00+08:00"),
	}
	got := task.Percent(mustTime("2026-06-23T17:00:00+08:00"))
	if got < 89.9 || got > 90.1 {
		t.Fatalf("percent = %.2f, want ~90", got)
	}
}

func TestInstantUsesCreatedAt(t *testing.T) {
	created := mustTime("2026-06-23T10:00:00+08:00")
	task := &Task{
		ID:        "t2",
		Type:      Instant,
		CreatedAt: created,
		EndAt:     mustTime("2026-06-23T11:00:00+08:00"),
	}
	got := task.Percent(mustTime("2026-06-23T10:30:00+08:00"))
	if got < 49.9 || got > 50.1 {
		t.Fatalf("percent = %.2f, want ~50", got)
	}
}

func TestClampBounds(t *testing.T) {
	start := mustTime("2026-06-23T08:00:00+08:00")
	task := &Task{Type: Scheduled, StartAt: &start, EndAt: mustTime("2026-06-23T09:00:00+08:00")}
	if p := task.Percent(mustTime("2026-06-23T07:00:00+08:00")); p != 0 {
		t.Fatalf("before start percent = %.2f, want 0", p)
	}
	if p := task.Percent(mustTime("2026-06-23T10:00:00+08:00")); p != 100 {
		t.Fatalf("after end percent = %.2f, want 100", p)
	}
}

// 验证 v2 分段：按 percent 升序，第 i 段 [p_{i-1}, p_i]，末段右边界 = 最大 percent，其后透明。
// 构造 a=30%、b=60%、c=90%（文档 §7.2 示例）。
func TestBuildLayoutV2Bands(t *testing.T) {
	// 同一起点 08:00，now=08:54（54min）；用不同窗口长度构造 a=30%、b=60%、c=90%。
	sA := mustTime("2026-06-23T08:00:00+08:00")
	sB := mustTime("2026-06-23T08:00:00+08:00")
	sC := mustTime("2026-06-23T08:00:00+08:00")
	now := mustTime("2026-06-23T08:54:00+08:00")

	tasks := []*Task{
		{ID: "c", Name: "C", Type: Scheduled, Color: "#FDD835", StartAt: &sC, EndAt: mustTime("2026-06-23T09:00:00+08:00")}, // 54/60  = 90%
		{ID: "b", Name: "B", Type: Scheduled, Color: "#43A047", StartAt: &sB, EndAt: mustTime("2026-06-23T09:30:00+08:00")}, // 54/90  = 60%
		{ID: "a", Name: "A", Type: Scheduled, Color: "#E53935", StartAt: &sA, EndAt: mustTime("2026-06-23T11:00:00+08:00")}, // 54/180 = 30%
	}

	layout := BuildLayout(tasks, now, noBehaviors)
	if !layout.HasActive {
		t.Fatal("expected active tasks")
	}
	if len(layout.Segments) != 3 {
		t.Fatalf("segments = %d, want 3", len(layout.Segments))
	}

	// 期望按 percent 升序：a(30) -> b(60) -> c(90)
	want := []struct {
		id               string
		start, end, fill float64
	}{
		{"a", 0, 30, 30},
		{"b", 30, 60, 60},
		{"c", 60, 90, 90},
	}
	for i, w := range want {
		s := layout.Segments[i]
		if s.TaskID != w.id || s.BarStart != w.start || s.BarEnd != w.end || s.FillEnd != w.fill || s.Percent != w.end {
			t.Fatalf("segment %d = %+v, want id=%s [%.0f,%.0f] fill=%.0f", i, s, w.id, w.start, w.end, w.fill)
		}
	}
	// 连续无间隙；末段右边界 = 90（其后透明，不生成色段）。
	for i := 1; i < len(layout.Segments); i++ {
		if layout.Segments[i].BarStart != layout.Segments[i-1].BarEnd {
			t.Fatalf("gap between segment %d and %d", i-1, i)
		}
	}
	if last := layout.Segments[len(layout.Segments)-1]; last.BarEnd != 90 {
		t.Fatalf("last segment barEnd = %.1f, want 90 (rest transparent)", last.BarEnd)
	}
}

// 仅一个任务活跃时，单段为 [0, percent]。
func TestBuildLayoutSingleActive(t *testing.T) {
	s1 := mustTime("2026-06-23T08:00:00+08:00")
	s2 := mustTime("2026-06-23T09:00:00+08:00")
	tasks := []*Task{
		{ID: "a", Name: "A", Type: Scheduled, Color: "#E53935", StartAt: &s1, EndAt: mustTime("2026-06-23T09:00:00+08:00")},
		{ID: "b", Name: "B", Type: Scheduled, Color: "#43A047", StartAt: &s2, EndAt: mustTime("2026-06-23T10:00:00+08:00")},
	}
	now := mustTime("2026-06-23T09:30:00+08:00") // a 过期，b 在 30min/60min = 50%
	// hide：到期任务被移除，仅余活跃的 b。
	layout := BuildLayout(tasks, now, hideBehaviors)
	if len(layout.Segments) != 1 {
		t.Fatalf("segments = %d, want 1 (only b active)", len(layout.Segments))
	}
	s := layout.Segments[0]
	if s.TaskID != "b" || s.BarStart != 0 || s.BarEnd != 50 {
		t.Fatalf("segment = %+v, want b [0,50]", s)
	}
}

// keep：到期任务保留为 100% 满色段，挂在活跃段之后并标记 Expired。
func TestBuildLayoutKeepsExpired(t *testing.T) {
	s1 := mustTime("2026-06-23T08:00:00+08:00")
	s2 := mustTime("2026-06-23T09:00:00+08:00")
	tasks := []*Task{
		{ID: "a", Name: "A", Type: Scheduled, Color: "#E53935", StartAt: &s1, EndAt: mustTime("2026-06-23T09:00:00+08:00")},
		{ID: "b", Name: "B", Type: Scheduled, Color: "#43A047", StartAt: &s2, EndAt: mustTime("2026-06-23T10:00:00+08:00")},
	}
	now := mustTime("2026-06-23T09:30:00+08:00") // a 过期；b 50%
	layout := BuildLayout(tasks, now, keepBehaviors)
	if len(layout.Segments) != 2 {
		t.Fatalf("segments = %d, want 2 (b active + a kept)", len(layout.Segments))
	}
	// b 在前（50%），a 保留在末端（100%）。
	if layout.Segments[0].TaskID != "b" || layout.Segments[0].BarEnd != 50 {
		t.Fatalf("segment0 = %+v, want b [.,50]", layout.Segments[0])
	}
	a := layout.Segments[1]
	if a.TaskID != "a" || a.BarStart != 50 || a.BarEnd != 100 || !a.Expired {
		t.Fatalf("segment1 = %+v, want a [50,100] expired", a)
	}
}

// KeepsVisibleWhenExpired：仅纯 hide 隐藏；其余（含空集合、blink+hide）保留。
func TestKeepsVisibleWhenExpired(t *testing.T) {
	cases := []struct {
		bs   []string
		want bool
	}{
		{nil, true},
		{[]string{BehaviorKeep}, true},
		{[]string{BehaviorHide}, false},
		{[]string{BehaviorHide, BehaviorNotify}, false},
		{[]string{BehaviorBlink, BehaviorHide}, true}, // blink 强制可见
		{[]string{BehaviorKeep, BehaviorHide}, true},
		{[]string{BehaviorNotify}, true},
	}
	for _, c := range cases {
		if got := KeepsVisibleWhenExpired(c.bs); got != c.want {
			t.Fatalf("KeepsVisibleWhenExpired(%v) = %v, want %v", c.bs, got, c.want)
		}
	}
}

// 每日循环任务：取「当天」时间窗 09:00–18:00 计算进度，不受锚点日期早于今天影响。
func TestRecurringDailyPercent(t *testing.T) {
	start := mustTime("2026-06-01T09:00:00+08:00") // 锚点在过去
	task := &Task{
		ID: "r", Type: Scheduled, StartAt: &start,
		EndAt:      mustTime("2026-06-01T18:00:00+08:00"),
		Recurrence: &Recurrence{Mode: RecurDaily},
	}
	now := mustTime("2026-06-25T17:00:00+08:00") // 当天 09:00–18:00 的 8/9 ≈ 88.9%
	if got := task.Percent(now); got < 88.8 || got > 89.0 {
		t.Fatalf("daily recurring percent = %.2f, want ~88.9", got)
	}
	if task.IsExpired(now) {
		t.Fatal("should be active within today's window")
	}
	// 窗口结束后到期。
	after := mustTime("2026-06-25T18:30:00+08:00")
	if !task.IsExpired(after) {
		t.Fatal("should be expired after today's window end")
	}
}

// 跨午夜循环任务：22:00–06:00，凌晨 03:00 应处于「前一天」开启的窗口内且活跃。
func TestRecurringOvernight(t *testing.T) {
	start := mustTime("2026-06-01T22:00:00+08:00")
	task := &Task{
		ID: "r", Type: Scheduled, StartAt: &start,
		EndAt:      mustTime("2026-06-01T06:00:00+08:00"), // 时分早于开始 → 跨午夜
		Recurrence: &Recurrence{Mode: RecurDaily},
	}
	now := mustTime("2026-06-25T03:00:00+08:00") // 24 日 22:00 → 25 日 06:00 窗口内，5/8=62.5%
	if got := task.Percent(now); got < 62.0 || got > 63.0 {
		t.Fatalf("overnight recurring percent = %.2f, want ~62.5", got)
	}
	if task.IsExpired(now) {
		t.Fatal("should be active inside overnight window")
	}
}

// 按星期循环：仅选中的星期为发生日。
func TestRecurringWeekly(t *testing.T) {
	start := mustTime("2026-06-01T09:00:00+08:00") // 2026-06-25 为周四(weekday=4)
	task := &Task{
		ID: "r", Type: Scheduled, StartAt: &start,
		EndAt:      mustTime("2026-06-01T18:00:00+08:00"),
		Recurrence: &Recurrence{Mode: RecurWeekly, Weekdays: []int{1, 3, 5}}, // 周一/三/五
	}
	thu := mustTime("2026-06-25T12:00:00+08:00") // 周四非发生日
	if _, _, started := task.windowAt(thu); started {
		// 周四非发生日：最近发生日为周三 24 日，其窗口 06-24 18:00 已结束 → 到期（keep 场景）。
		if !task.IsExpired(thu) {
			t.Fatal("thursday should fall outside an active window")
		}
	}
	fri := mustTime("2026-06-26T12:00:00+08:00") // 周五发生日，09-18 的 3/9≈33.3%
	if got := task.Percent(fri); got < 33.0 || got > 33.6 {
		t.Fatalf("friday percent = %.2f, want ~33.3", got)
	}
	if task.IsExpired(fri) {
		t.Fatal("friday should be active")
	}
}

// percent 相同的任务产生零宽段，应被跳过。
func TestBuildLayoutSkipsZeroWidth(t *testing.T) {
	s := mustTime("2026-06-23T08:00:00+08:00")
	tasks := []*Task{
		{ID: "a", Name: "A", Type: Scheduled, Color: "#E53935", StartAt: &s, EndAt: mustTime("2026-06-23T09:00:00+08:00")},
		{ID: "b", Name: "B", Type: Scheduled, Color: "#43A047", StartAt: &s, EndAt: mustTime("2026-06-23T09:00:00+08:00")},
	}
	now := mustTime("2026-06-23T08:30:00+08:00") // 两者均 50%
	layout := BuildLayout(tasks, now, noBehaviors)
	if len(layout.Segments) != 1 {
		t.Fatalf("segments = %d, want 1 (second is zero-width, skipped)", len(layout.Segments))
	}
	if layout.Segments[0].BarEnd != 50 {
		t.Fatalf("barEnd = %.1f, want 50", layout.Segments[0].BarEnd)
	}
}
