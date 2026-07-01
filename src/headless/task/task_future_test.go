package task

import "testing"

func TestFutureScheduledBeforeAndAfterStart(t *testing.T) {
	start := mustTime("2026-06-30T10:00:00+08:00")
	end := mustTime("2026-06-30T12:00:00+08:00")
	created := mustTime("2026-06-30T09:00:00+08:00")
	task := &Task{
		ID: "f", Name: "Future", Type: Scheduled, Color: "#E53935",
		StartAt: &start, EndAt: end, CreatedAt: created, Status: StatusActive,
	}
	before := mustTime("2026-06-30T09:30:00+08:00")
	atStart := mustTime("2026-06-30T10:00:00+08:00")
	after := mustTime("2026-06-30T10:30:00+08:00")
	pastEnd := mustTime("2026-06-30T13:00:00+08:00")

	_, _, startedBefore := task.windowAt(before)
	layoutBefore := BuildLayout([]*Task{task}, before, noBehaviors, testPosition)
	layoutAtStart := BuildLayout([]*Task{task}, atStart, noBehaviors, testPosition)
	layoutAfter := BuildLayout([]*Task{task}, after, noBehaviors, testPosition)
	layoutPastEnd := BuildLayout([]*Task{task}, pastEnd, noBehaviors, testPosition)

	if startedBefore {
		t.Fatal("before start: started should be false")
	}
	if len(layoutBefore.Segments) != 0 {
		t.Fatalf("before start: segments = %d, want 0", len(layoutBefore.Segments))
	}
	if len(layoutAtStart.Segments) != 1 {
		t.Fatalf("at start: segments = %d, want 1 (min render percent)", len(layoutAtStart.Segments))
	}
	if len(layoutAfter.Segments) != 1 {
		t.Fatalf("after start: segments = %d, want 1", len(layoutAfter.Segments))
	}
	if len(layoutPastEnd.Segments) != 1 || !layoutPastEnd.Segments[0].Expired {
		t.Fatalf("past end: want 1 expired segment, got %+v", layoutPastEnd.Segments)
	}
}
