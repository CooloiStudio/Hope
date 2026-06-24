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

// 验证多任务分段：等时长任务应平分顶栏并连续无间隙，末段对齐 100。
func TestBuildLayoutSegmentsContiguous(t *testing.T) {
	s1 := mustTime("2026-06-23T08:00:00+08:00")
	s2 := mustTime("2026-06-23T09:00:00+08:00")
	s3 := mustTime("2026-06-23T10:00:00+08:00")
	tasks := []*Task{
		{ID: "a", Name: "A", Type: Scheduled, Color: "#E53935", StartAt: &s1, EndAt: mustTime("2026-06-23T09:00:00+08:00")},
		{ID: "b", Name: "B", Type: Scheduled, Color: "#43A047", StartAt: &s2, EndAt: mustTime("2026-06-23T10:00:00+08:00")},
		{ID: "c", Name: "C", Type: Scheduled, Color: "#FDD835", StartAt: &s3, EndAt: mustTime("2026-06-23T11:00:00+08:00")},
	}
	now := mustTime("2026-06-23T10:30:00+08:00") // a、b 已过期，仅 c 活跃
	layout := BuildLayout(tasks, now)
	if !layout.HasActive {
		t.Fatal("expected active task")
	}
	if len(layout.Segments) != 1 {
		t.Fatalf("segments = %d, want 1 (only c active)", len(layout.Segments))
	}
	if layout.Segments[0].BarEnd != 100 {
		t.Fatalf("last segment barEnd = %.1f, want 100", layout.Segments[0].BarEnd)
	}

	now2 := mustTime("2026-06-23T08:30:00+08:00") // 三者均活跃
	layout2 := BuildLayout(tasks, now2)
	if len(layout2.Segments) != 3 {
		t.Fatalf("segments = %d, want 3", len(layout2.Segments))
	}
	if layout2.Segments[0].BarStart != 0 || layout2.Segments[2].BarEnd != 100 {
		t.Fatalf("layout not spanning full bar: %+v", layout2.Segments)
	}
	// 连续无间隙
	for i := 1; i < len(layout2.Segments); i++ {
		if layout2.Segments[i].BarStart != layout2.Segments[i-1].BarEnd {
			t.Fatalf("gap between segment %d and %d", i-1, i)
		}
	}
}
