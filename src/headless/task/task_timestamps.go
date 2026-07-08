package task

import (
	"errors"
	"time"
)

const secondsPerDay = 86400

// EnsureTimestamps 从 startTs/endTs 或旧版 startAt/endAt/createdAt 填充 Unix 秒字段（只读兼容路径）。
func (t *Task) EnsureTimestamps() {
	if t.EndTs == 0 && !t.EndAt.IsZero() {
		t.EndTs = t.EndAt.Unix()
	}
	if t.Type == Scheduled && t.StartTs == 0 && t.StartAt != nil && !t.StartAt.IsZero() {
		t.StartTs = t.StartAt.Unix()
	}
	if t.Type == Instant {
		if t.StartTs == 0 && !t.CreatedAt.IsZero() {
			t.StartTs = t.CreatedAt.Unix()
		}
		if t.EndTs == 0 && !t.EndAt.IsZero() {
			t.EndTs = t.EndAt.Unix()
		}
	}
	t.migrateLegacyCrossMidnight()
}

// migrateLegacyCrossMidnight 将旧版「截止时分早于开始时分」的跨午夜数据迁移为 endTs > startTs。
func (t *Task) migrateLegacyCrossMidnight() {
	if t.StartTs <= 0 || t.EndTs <= 0 {
		return
	}
	for t.EndTs <= t.StartTs {
		t.EndTs += secondsPerDay
	}
}

func (t *Task) effectiveStartTs() int64 {
	t.EnsureTimestamps()
	if t.Type == Scheduled && t.StartTs > 0 {
		return t.StartTs
	}
	if !t.CreatedAt.IsZero() {
		return t.CreatedAt.Unix()
	}
	if t.StartTs > 0 {
		return t.StartTs
	}
	return 0
}

func (t *Task) effectiveEndTs() int64 {
	t.EnsureTimestamps()
	return t.EndTs
}

func (t *Task) timeLocation() *time.Location {
	if t.StartAt != nil && !t.StartAt.IsZero() {
		return t.StartAt.Location()
	}
	if !t.EndAt.IsZero() {
		return t.EndAt.Location()
	}
	if !t.CreatedAt.IsZero() {
		return t.CreatedAt.Location()
	}
	return time.Local
}

// ValidateTimestamps 校验起止时间戳：startTs ≤ endTs 且 endTs > startTs（正时长）。
func (t *Task) ValidateTimestamps() error {
	t.EnsureTimestamps()
	startTs := t.effectiveStartTs()
	endTs := t.effectiveEndTs()
	if endTs <= 0 {
		return errors.New("endTs required")
	}
	if startTs <= 0 && t.Type == Scheduled {
		return errors.New("startTs required for scheduled task")
	}
	if startTs > endTs {
		return errors.New("startTs must be <= endTs")
	}
	if startTs == endTs {
		return errors.New("endTs must be after startTs")
	}
	return nil
}

// PrepareForPersist 落盘前规范化：确保时间戳并移除旧版 startAt/endAt 字段。
func (t *Task) PrepareForPersist() error {
	t.EnsureTimestamps()
	if err := t.ValidateTimestamps(); err != nil {
		return err
	}
	t.StartAt = nil
	t.EndAt = time.Time{}
	return nil
}

// RecurStepSeconds 返回循环任务完成时起止时间戳应累加的秒数（n×24×3600）。
func (t *Task) RecurStepSeconds() int64 {
	if t.Recurrence == nil {
		return 0
	}
	switch t.Recurrence.Mode {
	case RecurDaily:
		return secondsPerDay
	case RecurEveryN:
		n := t.Recurrence.Interval
		if n < 1 {
			n = 1
		}
		return int64(n) * secondsPerDay
	case RecurWeekly:
		return 7 * secondsPerDay
	default:
		return 0
	}
}
