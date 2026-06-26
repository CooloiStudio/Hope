package engine

import (
	"testing"
	"time"

	"hope/headless/config"
	"hope/headless/task"
)

func mustTime(s string) time.Time {
	t, err := time.Parse(time.RFC3339, s)
	if err != nil {
		panic(err)
	}
	return t
}

func makeTask(id string, pct time.Time, start, end time.Time) *task.Task {
	return &task.Task{
		ID:        id,
		Type:      task.Scheduled,
		StartAt:   &start,
		EndAt:     end,
		CreatedAt: start,
		Color:     "#FF6B35",
	}
}

func TestBuildAllFourLayoutClockwise(t *testing.T) {
	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	now := mustTime("2026-06-25T10:00:00+08:00") // 20%

	tasks := []*task.Task{makeTask("t1", now, start, end)}
	behaviorsOf := func(*task.Task) []string { return nil }

	segs := buildAllFourLayout(tasks, now, behaviorsOf, "clockwise")
	if len(segs) != 1 {
		t.Fatalf("expected 1 segment for 20%% task, got %d", len(segs))
	}
	s := segs[0]
	if s.Position != "top" {
		t.Errorf("expected position top, got %s", s.Position)
	}
	if s.BarStart != 0 || s.BarEnd != 80 || s.FillEnd != 80 {
		t.Errorf("expected top local 0-80 filled, got start=%.1f end=%.1f fill=%.1f", s.BarStart, s.BarEnd, s.FillEnd)
	}
}

func TestBuildAllFourLayoutWrapsTwoSides(t *testing.T) {
	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	now := mustTime("2026-06-25T14:30:00+08:00") // 65%

	tasks := []*task.Task{makeTask("t1", now, start, end)}
	behaviorsOf := func(*task.Task) []string { return nil }

	segs := buildAllFourLayout(tasks, now, behaviorsOf, "clockwise")
	if len(segs) != 3 {
		t.Fatalf("expected 3 segments for 65%% task, got %d", len(segs))
	}

	positions := []string{segs[0].Position, segs[1].Position, segs[2].Position}
	want := []string{"top", "right", "bottom"}
	for i, p := range want {
		if positions[i] != p {
			t.Errorf("segment %d position: want %s got %s", i, p, positions[i])
		}
	}

	// 0-25 global -> top 0-100 filled; 25-50 -> right 0-100 filled; 50-65 -> bottom 0-60 filled
	if segs[0].BarEnd != 100 || segs[0].FillEnd != 100 {
		t.Errorf("top segment: want end=100 fill=100, got end=%.1f fill=%.1f", segs[0].BarEnd, segs[0].FillEnd)
	}
	if segs[1].BarEnd != 100 || segs[1].FillEnd != 100 {
		t.Errorf("right segment: want end=100 fill=100, got end=%.1f fill=%.1f", segs[1].BarEnd, segs[1].FillEnd)
	}
	if segs[2].BarEnd < 55 || segs[2].BarEnd > 65 || segs[2].FillEnd != segs[2].BarEnd {
		t.Errorf("bottom segment: want end≈60 fill=end, got end=%.1f fill=%.1f", segs[2].BarEnd, segs[2].FillEnd)
	}
}

func TestBuildAllFourLayoutCounterClockwise(t *testing.T) {
	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	now := mustTime("2026-06-25T10:45:00+08:00") // 27.5%

	tasks := []*task.Task{makeTask("t1", now, start, end)}
	behaviorsOf := func(*task.Task) []string { return nil }

	segs := buildAllFourLayout(tasks, now, behaviorsOf, "counterClockwise")
	if len(segs) != 2 {
		t.Fatalf("expected 2 segments for 27.5%% task, got %d", len(segs))
	}
	if segs[0].Position != "top" || segs[1].Position != "left" {
		t.Errorf("expected top then left, got %s then %s", segs[0].Position, segs[1].Position)
	}
}

func TestCollectPositionsWithAdvancedOverride(t *testing.T) {
	settings := config.Settings{BarPosition: "top", AdvancedPosition: true}
	tasks := []*task.Task{
		{ID: "t1", Position: ""},
		{ID: "t2", Position: "left"},
		{ID: "t3", Position: "right"},
	}
	got := collectPositions(tasks, settings)
	want := []string{"top", "right", "left"}
	if len(got) != len(want) {
		t.Fatalf("collectPositions: want %v, got %v", want, got)
	}
	for i, p := range want {
		if got[i] != p {
			t.Errorf("position %d: want %s got %s", i, p, got[i])
		}
	}
}

func TestFilterTasksByPositionAdvanced(t *testing.T) {
	tasks := []*task.Task{
		{ID: "t1", Position: ""},
		{ID: "t2", Position: "left"},
		{ID: "t3", Position: "right"},
	}

	left := filterTasksByPosition(tasks, "left", "top", true)
	if len(left) != 1 || left[0].ID != "t2" {
		t.Errorf("left group: want [t2], got %v", ids(left))
	}

	top := filterTasksByPosition(tasks, "top", "top", true)
	if len(top) != 1 || top[0].ID != "t1" {
		t.Errorf("top group: want [t1], got %v", ids(top))
	}
}

func TestFilterTasksByPositionNoAdvanced(t *testing.T) {
	tasks := []*task.Task{
		{ID: "t1", Position: ""},
		{ID: "t2", Position: "left"},
	}
	all := filterTasksByPosition(tasks, "top", "top", false)
	if len(all) != 2 {
		t.Errorf("non-advanced: want both tasks on top, got %v", ids(all))
	}
}

func ids(tasks []*task.Task) []string {
	out := make([]string, len(tasks))
	for i, t := range tasks {
		out[i] = t.ID
	}
	return out
}
