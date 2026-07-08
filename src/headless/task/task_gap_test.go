package task

import "testing"

func TestRecurringWeeklyFixedWindowExpired(t *testing.T) {
	start := mustTime("2026-06-01T10:00:00+08:00")
	end := mustTime("2026-06-01T12:00:00+08:00")
	task := &Task{
		ID: "w", Type: Scheduled, Color: "#E53935",
		StartTs: start.Unix(), EndTs: end.Unix(),
		Recurrence: &Recurrence{Mode: RecurWeekly, Weekdays: []int{1, 3, 5}},
		Status:     StatusActive,
	}
	// 固定窗口已过期，与「今天是否周三」无关。
	tue := mustTime("2026-06-30T11:00:00+08:00")
	if !task.IsExpired(tue) {
		t.Fatal("fixed window should be expired long after end")
	}
}
