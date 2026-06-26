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
		Gif:       "test.gif",
	}
}

func TestBuildAllFourLayoutClockwise(t *testing.T) {
	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	now := mustTime("2026-06-25T10:00:00+08:00") // 20%

	tasks := []*task.Task{makeTask("t1", now, start, end)}
	behaviorsOf := func(*task.Task) []string { return nil }

	// 1920x1080：周长 6000，20% -> x=1200，落在顶边（顺时针 top->right->bottom->left）。
	segs := buildAllFourLayout(tasks, now, behaviorsOf, "top", "forward", 1920, 1080)
	if len(segs) != 1 {
		t.Fatalf("expected 1 segment for 20%% task, got %d", len(segs))
	}
	s := segs[0]
	if s.Position != "top" {
		t.Errorf("expected position top, got %s", s.Position)
	}
	wantEnd := 1200.0 / 1920.0 * 100.0 // 62.5
	if s.BarEnd < wantEnd-0.5 || s.BarEnd > wantEnd+0.5 || s.FillEnd != s.BarEnd {
		t.Errorf("expected top local end≈%.1f fill=end, got start=%.1f end=%.1f fill=%.1f", wantEnd, s.BarStart, s.BarEnd, s.FillEnd)
	}
	if s.Gif == "" {
		t.Errorf("active edge should carry the task gif")
	}
	if s.ImageRotation != 0 {
		t.Errorf("top edge rotation should be 0, got %.1f", s.ImageRotation)
	}
}

func TestBuildAllFourLayoutWrapsThreeSides(t *testing.T) {
	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	now := mustTime("2026-06-25T16:30:00+08:00") // 85%

	tasks := []*task.Task{makeTask("t1", now, start, end)}
	behaviorsOf := func(*task.Task) []string { return nil }

	// 1920x1080：周长 6000，85% -> x=5100 = 1920+1080+1920+180，落在左侧（顺时针顺序 top->right->bottom->left）。
	segs := buildAllFourLayout(tasks, now, behaviorsOf, "top", "forward", 1920, 1080)
	if len(segs) != 4 {
		t.Fatalf("expected 4 segments for 85%% task, got %d", len(segs))
	}

	positions := []string{segs[0].Position, segs[1].Position, segs[2].Position, segs[3].Position}
	want := []string{"top", "right", "bottom", "left"}
	for i, p := range want {
		if positions[i] != p {
			t.Errorf("segment %d position: want %s got %s", i, p, positions[i])
		}
	}

	rotations := []float64{0, 90, 180, 270}
	for i := 0; i < 4; i++ {
		if segs[i].ImageRotation != rotations[i] {
			t.Errorf("segment %d rotation: want %.1f got %.1f", i, rotations[i], segs[i].ImageRotation)
		}
	}

	for i := 0; i < 3; i++ {
		if segs[i].BarEnd != 100 || segs[i].FillEnd != 100 {
			t.Errorf("filled segment %d: want end=100 fill=100, got end=%.1f fill=%.1f", i, segs[i].BarEnd, segs[i].FillEnd)
		}
		if segs[i].Gif != "" {
			t.Errorf("filled segment %d should not carry gif", i)
		}
	}
	wantLeft := 180.0 / 1080.0 * 100.0 // 16.7
	if segs[3].BarEnd < wantLeft-0.5 || segs[3].BarEnd > wantLeft+0.5 || segs[3].FillEnd != segs[3].BarEnd {
		t.Errorf("left segment: want end≈%.1f fill=end, got end=%.1f fill=%.1f", wantLeft, segs[3].BarEnd, segs[3].FillEnd)
	}
	if segs[3].Gif == "" {
		t.Errorf("active edge should carry the task gif")
	}
}

func TestBuildAllFourLayoutCounterClockwise(t *testing.T) {
	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	now := mustTime("2026-06-25T11:15:00+08:00") // 195min/600min = 32.5% -> x=1950，越过顶边进入逆时针的左侧

	tasks := []*task.Task{makeTask("t1", now, start, end)}
	behaviorsOf := func(*task.Task) []string { return nil }

	segs := buildAllFourLayout(tasks, now, behaviorsOf, "top", "reverse", 1920, 1080)
	if len(segs) != 2 {
		t.Fatalf("expected 2 segments for 32.5%% counter-clockwise task, got %d", len(segs))
	}
	if segs[0].Position != "top" || segs[1].Position != "left" {
		t.Errorf("expected top then left, got %s then %s", segs[0].Position, segs[1].Position)
	}
	if segs[0].BarEnd != 100 || segs[0].Gif != "" {
		t.Errorf("top segment should be filled without gif")
	}
	if segs[1].Gif == "" {
		t.Errorf("left active segment should carry gif")
	}
	if segs[1].ImageRotation != 90 {
		t.Errorf("left edge rotation should be 90, got %.1f", segs[1].ImageRotation)
	}
}

func TestBuildAllFourLayoutFromBottom(t *testing.T) {
	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	now := mustTime("2026-06-25T11:00:00+08:00") // 30% -> x=1800，落在底边

	tasks := []*task.Task{makeTask("t1", now, start, end)}
	behaviorsOf := func(*task.Task) []string { return nil }

	// bottom + forward => counter-clockwise, order = bottom -> right -> top -> left
	segs := buildAllFourLayout(tasks, now, behaviorsOf, "bottom", "forward", 1920, 1080)
	if len(segs) != 1 {
		t.Fatalf("expected 1 segment, got %d", len(segs))
	}
	if segs[0].Position != "bottom" {
		t.Errorf("expected position bottom, got %s", segs[0].Position)
	}
	if segs[0].ImageRotation != 0 {
		t.Errorf("bottom edge rotation should be 0, got %.1f", segs[0].ImageRotation)
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
