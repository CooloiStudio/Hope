package config

import (
	"encoding/json"
	"testing"
	"time"

	"hope/headless/task"
)

func TestUpsertTaskPreservesCreatedAtOnUpdate(t *testing.T) {
	t.Setenv("APPDATA", t.TempDir())
	store, err := Load()
	if err != nil {
		t.Fatalf("load: %v", err)
	}

	created := time.Date(2026, 1, 2, 8, 0, 0, 0, time.Local)
	store.UpsertTask(&task.Task{
		ID:        "t1",
		Name:      "A",
		Color:     "#FF0000",
		Gif:       "C:\\pics\\a.gif",
		EndAt:     created.Add(time.Hour),
		CreatedAt: created,
	})

	store.UpsertTask(&task.Task{
		ID:       "t1",
		Name:     "A",
		Color:    "#FF0000",
		Position: "left",
		EndAt:    created.Add(time.Hour),
	})

	_, tasks := store.Snapshot()
	if len(tasks) != 1 {
		t.Fatalf("want 1 task, got %d", len(tasks))
	}
	if tasks[0].CreatedAt != created {
		t.Errorf("createdAt: want %v, got %v", created, tasks[0].CreatedAt)
	}
	if tasks[0].Gif != "" {
		t.Errorf("gif should be cleared when update payload omits it, got %q", tasks[0].Gif)
	}
}

func TestUnmarshalTaskUpdateWithoutGifClearsGif(t *testing.T) {
	var incoming task.Task
	if err := json.Unmarshal([]byte(`{"id":"t1","name":"A","color":"#f00","endAt":"2026-06-25T18:00:00+08:00","position":"left"}`), &incoming); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if incoming.Gif != "" {
		t.Fatalf("omitted gif should decode as empty string, got %q", incoming.Gif)
	}
}

func TestDirectionForAdvancedPerEdge(t *testing.T) {
	s := DefaultSettings()
	s.AdvancedPosition = true
	s.BarDirections = map[string]string{
		"top": "forward", "bottom": "reverse", "left": "forward", "right": "reverse",
	}
	if got := s.DirectionFor("bottom"); got != "reverse" {
		t.Errorf("bottom: want reverse, got %s", got)
	}
	if got := s.DirectionFor("left"); got != "forward" {
		t.Errorf("left: want forward, got %s", got)
	}
}

func TestDirectionForSingleBar(t *testing.T) {
	s := DefaultSettings()
	s.BarPosition = "top"
	s.BarDirection = "reverse"
	if got := s.DirectionFor("top"); got != "reverse" {
		t.Errorf("top: want reverse, got %s", got)
	}
	if got := s.DirectionFor("left"); got != "forward" {
		t.Errorf("non-global edge should default forward, got %s", got)
	}
}

func TestShowAdvancedSettingsPersisted(t *testing.T) {
	t.Setenv("APPDATA", t.TempDir())
	store, err := Load()
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	store.UpdateSettings(Settings{ShowAdvancedSettings: true})
	settings, _ := store.Snapshot()
	if !settings.ShowAdvancedSettings {
		t.Error("showAdvancedSettings should persist as true")
	}
}
