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

	layout := BuildLayout(tasks, now)
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
	layout := BuildLayout(tasks, now)
	if len(layout.Segments) != 1 {
		t.Fatalf("segments = %d, want 1 (only b active)", len(layout.Segments))
	}
	s := layout.Segments[0]
	if s.TaskID != "b" || s.BarStart != 0 || s.BarEnd != 50 {
		t.Fatalf("segment = %+v, want b [0,50]", s)
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
	layout := BuildLayout(tasks, now)
	if len(layout.Segments) != 1 {
		t.Fatalf("segments = %d, want 1 (second is zero-width, skipped)", len(layout.Segments))
	}
	if layout.Segments[0].BarEnd != 50 {
		t.Fatalf("barEnd = %.1f, want 50", layout.Segments[0].BarEnd)
	}
}
