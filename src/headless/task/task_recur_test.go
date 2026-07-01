package task

import "testing"

func TestRecurringDailyBeforeTodayWindow(t *testing.T) {
    start := mustTime("2026-06-01T10:00:00+08:00")
    end := mustTime("2026-06-01T12:00:00+08:00")
    task := &Task{
        ID: "r", Name: "Daily", Type: Scheduled, Color: "#E53935",
        StartAt: &start, EndAt: end,
        Recurrence: &Recurrence{Mode: RecurDaily},
        Status: StatusActive,
    }
    before := mustTime("2026-06-30T09:30:00+08:00")
    during := mustTime("2026-06-30T11:00:00+08:00")
    after := mustTime("2026-06-30T13:00:00+08:00")

	layoutBefore := BuildLayout([]*Task{task}, before, noBehaviors, testPosition)
	layoutDuring := BuildLayout([]*Task{task}, during, noBehaviors, testPosition)
	layoutAfter := BuildLayout([]*Task{task}, after, noBehaviors, testPosition)

	if task.HasStarted(before) || len(layoutBefore.Segments) != 0 {
		t.Fatalf("before today's window: started=%v segs=%d, want false/0", task.HasStarted(before), len(layoutBefore.Segments))
	}
	if len(layoutDuring.Segments) != 1 {
		t.Fatalf("during window: segments = %d, want 1", len(layoutDuring.Segments))
	}
	if len(layoutAfter.Segments) != 1 || !layoutAfter.Segments[0].Expired {
		t.Fatalf("after window: want 1 expired segment, got %+v", layoutAfter.Segments)
	}
}
