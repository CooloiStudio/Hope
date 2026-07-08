package task

import "testing"

func TestEnsureTimestampsFromLegacyRFC3339(t *testing.T) {
	start := mustTime("2026-07-06T08:00:00+08:00")
	end := mustTime("2026-07-06T23:00:00+08:00")
	task := &Task{
		Type: Scheduled, StartAt: &start, EndAt: end,
	}
	task.EnsureTimestamps()
	if task.StartTs != start.Unix() || task.EndTs != end.Unix() {
		t.Fatalf("migrate ts: startTs=%d endTs=%d", task.StartTs, task.EndTs)
	}
}

func TestMigrateLegacyCrossMidnightEnd(t *testing.T) {
	start := mustTime("2026-06-01T22:00:00+08:00")
	end := mustTime("2026-06-01T06:00:00+08:00") // 旧版同日跨午夜
	task := &Task{Type: Scheduled, StartAt: &start, EndAt: end}
	task.EnsureTimestamps()
	if task.EndTs <= task.StartTs {
		t.Fatal("cross-midnight legacy should bump endTs above startTs")
	}
}

func TestPrepareForPersistStripsLegacyFields(t *testing.T) {
	start := mustTime("2026-06-01T08:00:00+08:00")
	end := mustTime("2026-06-01T18:00:00+08:00")
	task := &Task{Type: Scheduled, StartAt: &start, EndAt: end}
	if err := task.PrepareForPersist(); err != nil {
		t.Fatal(err)
	}
	if task.StartAt != nil || !task.EndAt.IsZero() {
		t.Fatal("legacy fields should be cleared on persist")
	}
	if task.StartTs == 0 || task.EndTs == 0 {
		t.Fatal("timestamps should remain")
	}
}
