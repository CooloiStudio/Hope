package task

import (
	"testing"
	"time"
)

func TestRecurringFixedWindowExpiredAfterEnd(t *testing.T) {
	start := mustTime("2026-07-06T08:00:00+08:00")
	end := mustTime("2026-07-06T23:00:00+08:00")
	task := &Task{
		ID: "r", Name: "Daily", Type: Scheduled, Color: "#E53935",
		StartTs: start.Unix(), EndTs: end.Unix(),
		Recurrence: &Recurrence{Mode: RecurDaily},
		Status:     StatusActive,
	}
	// 次日：窗口已结束，不自动滚到「今天」的新窗。
	nextDay := mustTime("2026-07-07T10:13:00+08:00")
	if !task.IsExpired(nextDay) {
		t.Fatal("next day after window end should be expired")
	}
	if p := task.Percent(nextDay); p != 100 {
		t.Fatalf("expired percent = %.2f, want 100", p)
	}
	layout := BuildLayout([]*Task{task}, nextDay, noBehaviors, testPosition)
	if len(layout.Segments) != 1 || !layout.Segments[0].Expired {
		t.Fatalf("want 1 expired segment, got %+v", layout.Segments)
	}
}

func TestRecurringDailyWithinWindow(t *testing.T) {
	start := mustTime("2026-06-01T10:00:00+08:00")
	end := mustTime("2026-06-01T12:00:00+08:00")
	task := &Task{
		ID: "r", Name: "Daily", Type: Scheduled, Color: "#E53935",
		StartTs: start.Unix(), EndTs: end.Unix(),
		Recurrence: &Recurrence{Mode: RecurDaily},
		Status:     StatusActive,
	}
	before := mustTime("2026-06-01T09:30:00+08:00")
	during := mustTime("2026-06-01T11:00:00+08:00")
	after := mustTime("2026-06-01T13:00:00+08:00")

	if task.HasStarted(before) {
		t.Fatal("before start: should not have started")
	}
	if len(BuildLayout([]*Task{task}, before, noBehaviors, testPosition).Segments) != 0 {
		t.Fatal("before start: no segments")
	}
	if len(BuildLayout([]*Task{task}, during, noBehaviors, testPosition).Segments) != 1 {
		t.Fatal("during: want active segment")
	}
	layoutAfter := BuildLayout([]*Task{task}, after, noBehaviors, testPosition)
	if len(layoutAfter.Segments) != 1 || !layoutAfter.Segments[0].Expired {
		t.Fatalf("after end: want expired segment, got %+v", layoutAfter.Segments)
	}
}

func TestDurationRecurringAdvanceUsesCompletionTime(t *testing.T) {
	// 倒计时循环：起点=创建时刻，时长 2 小时。
	created := mustTime("2026-07-06T08:00:00+08:00")
	task := &Task{
		ID: "c", Name: "Countdown", Type: Instant, Color: "#1E88E5",
		CreatedAt:  created,
		EndTs:      created.Add(2 * time.Hour).Unix(),
		Recurrence: &Recurrence{Mode: RecurDuration},
		Status:     StatusActive,
	}
	if !task.IsRecurring() {
		t.Fatal("duration instant task should be recurring")
	}
	// 完成时刻晚于原截止：新一期应以完成时刻为起点，保持 2 小时时长。
	done := mustTime("2026-07-06T11:30:00+08:00")
	next := task.Clone()
	if !next.AdvanceIfRecurring(done) {
		t.Fatal("advance duration failed")
	}
	if next.StartTs != done.Unix() {
		t.Fatalf("next startTs=%d, want %d", next.StartTs, done.Unix())
	}
	if next.EndTs != done.Unix()+int64(2*time.Hour/time.Second) {
		t.Fatalf("next endTs=%d, want %d", next.EndTs, done.Unix()+7200)
	}
}

func TestScheduledDurationModeNotRecurring(t *testing.T) {
	// duration 模式只对倒计时（instant）有效，定时任务不应被视为循环。
	start := mustTime("2026-07-06T08:00:00+08:00")
	task := &Task{
		ID: "s", Type: Scheduled,
		StartTs: start.Unix(), EndTs: start.Add(time.Hour).Unix(),
		Recurrence: &Recurrence{Mode: RecurDuration},
	}
	if task.IsRecurring() {
		t.Fatal("scheduled + duration must not be recurring")
	}
}

func TestAdvanceIfRecurringAddsStep(t *testing.T) {
	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	task := &Task{
		ID: "r", Type: Scheduled,
		StartTs: start.Unix(), EndTs: end.Unix(),
		Recurrence: &Recurrence{Mode: RecurDaily},
	}
	if !task.AdvanceIfRecurring(mustTime("2026-06-25T20:00:00+08:00")) {
		t.Fatal("advance failed")
	}
	wantStart := start.Unix() + 86400
	wantEnd := end.Unix() + 86400
	if task.StartTs != wantStart || task.EndTs != wantEnd {
		t.Fatalf("after advance: startTs=%d endTs=%d, want %d/%d", task.StartTs, task.EndTs, wantStart, wantEnd)
	}
}
