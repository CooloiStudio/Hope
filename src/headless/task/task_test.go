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
func keepBehaviors(*Task) []string { return []string{BehaviorKeep} }

const testPosition = "top"

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

	layout := BuildLayout(tasks, now, noBehaviors, testPosition)
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

// 已完成任务不渲染：仅余活跃任务时，单段为 [0, percent]。
func TestBuildLayoutSingleActive(t *testing.T) {
	s1 := mustTime("2026-06-23T08:00:00+08:00")
	s2 := mustTime("2026-06-23T09:00:00+08:00")
	tasks := []*Task{
		{ID: "a", Name: "A", Type: Scheduled, Color: "#E53935", StartAt: &s1, EndAt: mustTime("2026-06-23T09:00:00+08:00"), Completed: true},
		{ID: "b", Name: "B", Type: Scheduled, Color: "#43A047", StartAt: &s2, EndAt: mustTime("2026-06-23T10:00:00+08:00")},
	}
	now := mustTime("2026-06-23T09:30:00+08:00") // a 已完成（不渲染），b 在 30min/60min = 50%
	layout := BuildLayout(tasks, now, noBehaviors, testPosition)
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
	layout := BuildLayout(tasks, now, keepBehaviors, testPosition)
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

// 多个已到期任务：各自铺满整条（0..100，互相重叠），保留全部颜色供前端做轮换呼吸；
// 不应因同为 100% 的零宽嵌套而被合并成单段。
func TestBuildLayoutMultipleExpiredOverlap(t *testing.T) {
	s := mustTime("2026-06-23T08:00:00+08:00")
	e := mustTime("2026-06-23T09:00:00+08:00")
	tasks := []*Task{
		{ID: "a", Color: "#E53935", Type: Scheduled, StartAt: &s, EndAt: e},
		{ID: "b", Color: "#43A047", Type: Scheduled, StartAt: &s, EndAt: e},
		{ID: "c", Color: "#1E88E5", Type: Scheduled, StartAt: &s, EndAt: e},
	}
	now := mustTime("2026-06-23T10:00:00+08:00") // 三者均已到期
	layout := BuildLayout(tasks, now, keepBehaviors, testPosition)
	if len(layout.Segments) != 3 {
		t.Fatalf("segments = %d, want 3 (each expired task full bar)", len(layout.Segments))
	}
	seen := map[string]bool{}
	for _, seg := range layout.Segments {
		if seg.BarStart != 0 || seg.BarEnd != 100 || seg.FillEnd != 100 || !seg.Expired {
			t.Fatalf("segment %+v, want [0,100] expired full bar", seg)
		}
		seen[seg.Color] = true
	}
	if len(seen) != 3 {
		t.Fatalf("distinct colors = %d, want 3", len(seen))
	}
}

// KeepsVisibleWhenExpired：新模型到期默认自动显示，恒为 true（已移除「自动隐藏」）。
func TestKeepsVisibleWhenExpired(t *testing.T) {
	cases := [][]string{
		nil,
		{},
		{BehaviorBlink},
		{BehaviorNotify},
		{BehaviorCelebrate},
		{BehaviorBlink, BehaviorNotify},
	}
	for _, bs := range cases {
		if got := KeepsVisibleWhenExpired(bs); !got {
			t.Fatalf("KeepsVisibleWhenExpired(%v) = false, want true", bs)
		}
	}
}

// 每日循环任务：进度仅由存储的 startTs/endTs 决定，过期后不自动进入「今天」新窗。
func TestRecurringDailyPercent(t *testing.T) {
	start := mustTime("2026-06-01T09:00:00+08:00")
	end := mustTime("2026-06-01T18:00:00+08:00")
	task := &Task{
		ID: "r", Type: Scheduled,
		StartTs: start.Unix(), EndTs: end.Unix(),
		Recurrence: &Recurrence{Mode: RecurDaily},
	}
	now := mustTime("2026-06-25T17:00:00+08:00")
	if !task.IsExpired(now) {
		t.Fatal("should be expired: stored window ended long ago")
	}
	after := mustTime("2026-06-25T18:30:00+08:00")
	if !task.IsExpired(after) {
		t.Fatal("should be expired after window end")
	}
}

// 跨午夜：起止戳直接表示绝对窗口（截止日在次日）。
func TestRecurringOvernight(t *testing.T) {
	start := mustTime("2026-06-24T22:00:00+08:00")
	end := mustTime("2026-06-25T06:00:00+08:00")
	task := &Task{
		ID: "r", Type: Scheduled,
		StartTs: start.Unix(), EndTs: end.Unix(),
		Recurrence: &Recurrence{Mode: RecurDaily},
	}
	now := mustTime("2026-06-25T03:00:00+08:00") // 5/8=62.5%
	if got := task.Percent(now); got < 62.0 || got > 63.0 {
		t.Fatalf("overnight percent = %.2f, want ~62.5", got)
	}
	if task.IsExpired(now) {
		t.Fatal("should be active inside overnight window")
	}
}

// 按星期循环：运行期仍用固定 startTs/endTs；weekly 仅在完成时 +7 天推进。
func TestRecurringWeekly(t *testing.T) {
	start := mustTime("2026-06-26T09:00:00+08:00") // 周五
	end := mustTime("2026-06-26T18:00:00+08:00")
	task := &Task{
		ID: "r", Type: Scheduled,
		StartTs: start.Unix(), EndTs: end.Unix(),
		Recurrence: &Recurrence{Mode: RecurWeekly, Weekdays: []int{1, 3, 5}},
	}
	fri := mustTime("2026-06-26T12:00:00+08:00") // 3/9≈33.3%
	if got := task.Percent(fri); got < 33.0 || got > 33.6 {
		t.Fatalf("friday percent = %.2f, want ~33.3", got)
	}
	if task.IsExpired(fri) {
		t.Fatal("friday should be active within stored window")
	}
	thu := mustTime("2026-06-25T12:00:00+08:00")
	if task.HasStarted(thu) {
		t.Fatal("thursday before window should not have started")
	}
	if task.IsExpired(thu) {
		t.Fatal("thursday before window should not be expired")
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
	layout := BuildLayout(tasks, now, noBehaviors, testPosition)
	if len(layout.Segments) != 1 {
		t.Fatalf("segments = %d, want 1 (second is zero-width, skipped)", len(layout.Segments))
	}
	if layout.Segments[0].BarEnd != 50 {
		t.Fatalf("barEnd = %.1f, want 50", layout.Segments[0].BarEnd)
	}
}
