package task

import "testing"

func TestRecurringWeeklyGapDay(t *testing.T) {
    start := mustTime("2026-06-01T10:00:00+08:00")
    end := mustTime("2026-06-01T12:00:00+08:00")
    task := &Task{
        ID: "w", Type: Scheduled, Color: "#E53935",
        StartAt: &start, EndAt: end,
        Recurrence: &Recurrence{Mode: RecurWeekly, Weekdays: []int{1,3,5}},
        Status: StatusActive,
    }
    // 2026-06-30 is Tuesday (weekday=2), not Mon/Wed/Fri
    tue := mustTime("2026-06-30T11:00:00+08:00")
    _, _, started := task.windowAt(tue)
    layout := BuildLayout([]*Task{task}, tue, noBehaviors, testPosition)
    t.Logf("tuesday 11:00: started=%v expired=%v pct=%.1f segs=%d", started, task.IsExpired(tue), task.Percent(tue), len(layout.Segments))
}
