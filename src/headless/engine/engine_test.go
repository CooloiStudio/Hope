package engine

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"hope/headless/config"
	"hope/headless/ipc"
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

// 低进度覆盖高进度：四边环绕下重叠区由 z 序决定，低进度段须最后生成（绘制在最上层）。
func TestBuildAllFourLayoutLowCoversHigh(t *testing.T) {
	start := mustTime("2026-06-25T08:00:00+08:00")
	now := mustTime("2026-06-25T10:00:00+08:00")
	tLow := makeTask("low", now, start, mustTime("2026-06-25T18:00:00+08:00"))   // 20%
	tHigh := makeTask("high", now, start, mustTime("2026-06-25T10:30:00+08:00")) // 80%
	behaviorsOf := func(*task.Task) []string { return nil }

	// 输入顺序故意是 low 在前、high 在后；修复后应按 pct 降序输出（high 先、low 后）。
	segs := buildAllFourLayout([]*task.Task{tLow, tHigh}, now, behaviorsOf, "top", "forward", 1920, 1080)
	if len(segs) == 0 {
		t.Fatalf("expected segments, got 0")
	}
	if segs[0].TaskID != "high" {
		t.Errorf("first (bottom z) segment should be high-progress, got %s", segs[0].TaskID)
	}
	if segs[len(segs)-1].TaskID != "low" {
		t.Errorf("last (top z) segment should be low-progress, got %s", segs[len(segs)-1].TaskID)
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

	// 顺时针：起点 top=0°，每进入下一条边再转 90° → 0/90/180/270
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

	// top + reverse => 逆时针，顺序 top -> left -> bottom -> right
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
	// 逆时针：起点 top=0°，进入第 2 条边(left) 逆向再转 90° → -90° 归一化 270°
	if segs[1].ImageRotation != 270 {
		t.Errorf("left edge rotation should be 270 (idx1, ccw), got %.1f", segs[1].ImageRotation)
	}
}

func TestBuildAllFourLayoutFromBottom(t *testing.T) {
	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	now := mustTime("2026-06-25T11:00:00+08:00") // 30% -> x=1800，落在底边

	tasks := []*task.Task{makeTask("t1", now, start, end)}
	behaviorsOf := func(*task.Task) []string { return nil }

	// bottom + forward => 逆时针，顺序 = bottom -> right -> top -> left
	// 起点是 bottom（idx=0），保持原图朝向，旋转 = 0°
	segs := buildAllFourLayout(tasks, now, behaviorsOf, "bottom", "forward", 1920, 1080)
	if len(segs) != 1 {
		t.Fatalf("expected 1 segment, got %d", len(segs))
	}
	if segs[0].Position != "bottom" {
		t.Errorf("expected position bottom, got %s", segs[0].Position)
	}
	if segs[0].ImageRotation != 0 {
		t.Errorf("bottom start edge rotation should be 0, got %.1f", segs[0].ImageRotation)
	}
}

// TestDeriveSurroundDir 验证 8 种起点+方向 → 环绕方向的推导。
func TestDeriveSurroundDir(t *testing.T) {
	tests := []struct {
		startPos string
		fillDir  string
		want     string
	}{
		{"top", "forward", "cw"},
		{"top", "reverse", "ccw"},
		{"bottom", "forward", "ccw"},
		{"bottom", "reverse", "cw"},
		{"left", "forward", "ccw"},
		{"left", "reverse", "cw"},
		{"right", "forward", "cw"},
		{"right", "reverse", "ccw"},
	}
	for _, tc := range tests {
		got := deriveSurroundDir(tc.startPos, tc.fillDir)
		if got != tc.want {
			t.Errorf("deriveSurroundDir(%s,%s) = %s, want %s", tc.startPos, tc.fillDir, got, tc.want)
		}
	}
}

// TestDeriveAllFourOrders 验证 8 种组合的边的顺序和 x 区间边界。
// 1920x1080 屏幕：w=1920, h=1080, perim=6000。
//
// 顺时针基准序列：top → right → bottom → left
//   旋转起点到 startPos 即为该起点的顺时针环绕顺序。
// 逆时针基准序列：top → left → bottom → right
//   旋转起点到 startPos 即为该起点的逆时针环绕顺序。
func TestDeriveAllFourOrders(t *testing.T) {
	tests := []struct {
		startPos string
		baseDir  string
		want     []string
	}{
		// top+forward = 顺时针
		{"top", "forward", []string{"top", "right", "bottom", "left"}},
		// top+reverse = 逆时针
		{"top", "reverse", []string{"top", "left", "bottom", "right"}},
		// bottom+forward = 逆时针；逆时针基准序列旋转使 bottom 在前
		// 基准 top→left→bottom→right，bottom 在 [2]，旋转后 = bottom→right→top→left
		{"bottom", "forward", []string{"bottom", "right", "top", "left"}},
		// bottom+reverse = 顺时针；顺时针基准序列旋转使 bottom 在前
		// 基准 top→right→bottom→left，bottom 在 [2]，旋转后 = bottom→left→top→right
		{"bottom", "reverse", []string{"bottom", "left", "top", "right"}},
		// left+forward = 逆时针；逆时针基准旋转使 left 在前
		// 基准 top→left→bottom→right，left 在 [1]，旋转后 = left→bottom→right→top
		{"left", "forward", []string{"left", "bottom", "right", "top"}},
		// left+reverse = 顺时针；顺时针基准旋转使 left 在前
		// 基准 top→right→bottom→left，left 在 [3]，旋转后 = left→top→right→bottom
		{"left", "reverse", []string{"left", "top", "right", "bottom"}},
		// right+forward = 顺时针；顺时针基准旋转使 right 在前
		// 基准 top→right→bottom→left，right 在 [1]，旋转后 = right→bottom→left→top
		{"right", "forward", []string{"right", "bottom", "left", "top"}},
		// right+reverse = 逆时针；逆时针基准旋转使 right 在前
		// 基准 top→left→bottom→right，right 在 [3]，旋转后 = right→top→left→bottom
		{"right", "reverse", []string{"right", "top", "left", "bottom"}},
	}
	for _, tc := range tests {
		got := deriveAllFourOrders(tc.startPos, tc.baseDir)
		if len(got) != 4 {
			t.Fatalf("deriveAllFourOrders(%s,%s): want 4 edges, got %d", tc.startPos, tc.baseDir, len(got))
		}
		for i := 0; i < 4; i++ {
			if got[i] != tc.want[i] {
				t.Errorf("deriveAllFourOrders(%s,%s)[%d]: want %s, got %s", tc.startPos, tc.baseDir, i, tc.want[i], got[i])
			}
		}
	}
}

// TestEdgeLen 验证边长计算。
func TestEdgeLen(t *testing.T) {
	if edgeLen("top", 1920, 1080) != 1920 {
		t.Errorf("top edge len should be w")
	}
	if edgeLen("bottom", 1920, 1080) != 1920 {
		t.Errorf("bottom edge len should be w")
	}
	if edgeLen("left", 1920, 1080) != 1080 {
		t.Errorf("left edge len should be h")
	}
	if edgeLen("right", 1920, 1080) != 1080 {
		t.Errorf("right edge len should be h")
	}
}

// TestRotationForSurround 验证按环绕顺序累加的旋转角度。
func TestRotationForSurround(t *testing.T) {
	cw := []float64{0, 90, 180, 270}
	ccw := []float64{0, 270, 180, 90}
	for i := 0; i < 4; i++ {
		if got := rotationForSurround("cw", i); got != cw[i] {
			t.Errorf("rotationForSurround(cw,%d) = %.1f, want %.1f", i, got, cw[i])
		}
		if got := rotationForSurround("ccw", i); got != ccw[i] {
			t.Errorf("rotationForSurround(ccw,%d) = %.1f, want %.1f", i, got, ccw[i])
		}
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

// TestUpdateSettingsPersists 验证 updateSettings 命令会把设置写入 config.json。
func TestUpdateSettingsPersists(t *testing.T) {
	t.Setenv("APPDATA", t.TempDir())
	store, err := config.Load()
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	eng := New(store, nil)
	raw := json.RawMessage(`{"barHeightPx":7,"barPosition":"bottom","allFour":true}`)
	eng.HandleCommand(ipc.Command{Action: "updateSettings", Settings: raw})

	path := filepath.Join(config.Dir(), "config.json")
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("config.json not written: %v", err)
	}
	var sf struct {
		Settings config.Settings `json:"settings"`
	}
	if err := json.Unmarshal(b, &sf); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if sf.Settings.BarHeightPx != 7 || sf.Settings.BarPosition != "bottom" || !sf.Settings.AllFour {
		t.Errorf("persisted settings wrong: %+v", sf.Settings)
	}
}

// TestCompleteNonRecurringMarksCompleted 验证完成非循环任务会置为已完成且不再渲染。
func TestCompleteNonRecurringMarksCompleted(t *testing.T) {
	t.Setenv("APPDATA", t.TempDir())
	store, err := config.Load()
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	eng := New(store, nil)

	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	tk := makeTask("t1", start, start, end)
	eng.HandleCommand(ipc.Command{Action: "createTask", Task: tk})
	eng.HandleCommand(ipc.Command{Action: "completeTask", TaskID: "t1"})

	_, tasks := store.Snapshot()
	if len(tasks) != 1 {
		t.Fatalf("expected 1 task, got %d", len(tasks))
	}
	if !tasks[0].IsCompleted() || tasks[0].Status != task.StatusCompleted {
		t.Errorf("task should be completed, got status=%q completed=%v", tasks[0].Status, tasks[0].Completed)
	}
	if tasks[0].CompletedAt == nil {
		t.Errorf("completedAt should be set")
	}
}

// TestCompleteRecurringSpawnsNextCopy 验证完成循环任务会生成下一期进行中副本，并把原任务置为已完成。
func TestCompleteRecurringSpawnsNextCopy(t *testing.T) {
	t.Setenv("APPDATA", t.TempDir())
	store, err := config.Load()
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	eng := New(store, nil)

	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	tk := makeTask("r1", start, start, end)
	tk.Recurrence = &task.Recurrence{Mode: task.RecurDaily}
	eng.HandleCommand(ipc.Command{Action: "createTask", Task: tk})
	eng.HandleCommand(ipc.Command{Action: "completeTask", TaskID: "r1"})

	_, tasks := store.Snapshot()
	if len(tasks) != 2 {
		t.Fatalf("expected original + copy = 2 tasks, got %d", len(tasks))
	}
	var orig, copyT *task.Task
	for _, tt := range tasks {
		if tt.ID == "r1" {
			orig = tt
		} else {
			copyT = tt
		}
	}
	if orig == nil || !orig.IsCompleted() {
		t.Fatalf("original task should be completed")
	}
	if copyT == nil {
		t.Fatalf("a new active copy should exist")
	}
	if copyT.IsCompleted() || copyT.Status != task.StatusActive {
		t.Errorf("copy should be active, got status=%q", copyT.Status)
	}
	if copyT.StartAt == nil || !copyT.StartAt.After(start) {
		t.Errorf("copy start should be advanced past original start, got %v", copyT.StartAt)
	}
	// 副本保留全部参数（颜色、循环规则）。
	if copyT.Color != orig.Color || copyT.Recurrence == nil || copyT.Recurrence.Mode != task.RecurDaily {
		t.Errorf("copy should retain params: color=%q recurrence=%+v", copyT.Color, copyT.Recurrence)
	}
}

// TestCompleteRecurringIdempotent 重复完成同一循环任务不应再次生成下一期副本。
func TestCompleteRecurringIdempotent(t *testing.T) {
	t.Setenv("APPDATA", t.TempDir())
	store, err := config.Load()
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	eng := New(store, nil)

	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	tk := makeTask("r1", start, start, end)
	tk.Recurrence = &task.Recurrence{Mode: task.RecurDaily}
	eng.HandleCommand(ipc.Command{Action: "createTask", Task: tk})
	eng.HandleCommand(ipc.Command{Action: "completeTask", TaskID: "r1"})
	eng.HandleCommand(ipc.Command{Action: "completeTask", TaskID: "r1"})

	_, tasks := store.Snapshot()
	if len(tasks) != 2 {
		t.Fatalf("expected original + one copy = 2 tasks, got %d", len(tasks))
	}
}

// TestUpdateTaskCannotUncomplete 已完成任务不应被 updateTask 重置为进行中。
func TestUpdateTaskCannotUncomplete(t *testing.T) {
	t.Setenv("APPDATA", t.TempDir())
	store, err := config.Load()
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	eng := New(store, nil)

	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	tk := makeTask("t1", start, start, end)
	eng.HandleCommand(ipc.Command{Action: "createTask", Task: tk})
	eng.HandleCommand(ipc.Command{Action: "completeTask", TaskID: "t1"})

	reset := makeTask("t1", start, start, end)
	reset.Status = task.StatusActive
	reset.Completed = false
	reset.CompletedAt = nil
	eng.HandleCommand(ipc.Command{Action: "updateTask", Task: reset})

	_, tasks := store.Snapshot()
	if len(tasks) != 1 {
		t.Fatalf("expected 1 task, got %d", len(tasks))
	}
	if !tasks[0].IsCompleted() {
		t.Errorf("completed task must stay completed after updateTask, got status=%q", tasks[0].Status)
	}
}

// TestDeleteCompletedTasks 验证一键删除仅移除已完成任务。
func TestDeleteCompletedTasks(t *testing.T) {
	t.Setenv("APPDATA", t.TempDir())
	store, err := config.Load()
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	eng := New(store, nil)

	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	a := makeTask("a", start, start, end)
	b := makeTask("b", start, start, end)
	eng.HandleCommand(ipc.Command{Action: "createTask", Task: a})
	eng.HandleCommand(ipc.Command{Action: "createTask", Task: b})
	eng.HandleCommand(ipc.Command{Action: "completeTask", TaskID: "a"})
	eng.HandleCommand(ipc.Command{Action: "deleteCompletedTasks"})

	_, tasks := store.Snapshot()
	if len(tasks) != 1 || tasks[0].ID != "b" {
		t.Fatalf("expected only active task b to remain, got %v", ids(tasks))
	}
}

func ids(tasks []*task.Task) []string {
	out := make([]string, len(tasks))
	for i, t := range tasks {
		out[i] = t.ID
	}
	return out
}

// TestMutatingCommandsReturnTasksSnapshot 写操作应单播最新 tasks，避免 Desktop 再发 listTasks 竞态。
func TestMutatingCommandsReturnTasksSnapshot(t *testing.T) {
	t.Setenv("APPDATA", t.TempDir())
	store, err := config.Load()
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	eng := New(store, nil)

	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	tk := makeTask("t1", start, start, end)
	eng.HandleCommand(ipc.Command{Action: "createTask", Task: tk})

	resp := eng.HandleCommand(ipc.Command{Action: "completeTask", TaskID: "t1", RequestID: "req-1"})
	m, ok := resp.(map[string]any)
	if !ok {
		t.Fatalf("completeTask should return map, got %T", resp)
	}
	if m["type"] != "tasks" {
		t.Fatalf("expected type=tasks, got %v", m["type"])
	}
	if m["requestId"] != "req-1" {
		t.Fatalf("expected requestId echo, got %v", m["requestId"])
	}
	tasks, ok := m["tasks"].([]*task.Task)
	if !ok {
		t.Fatalf("tasks field type %T", m["tasks"])
	}
	if len(tasks) != 1 || !tasks[0].IsCompleted() {
		t.Fatalf("expected one completed task in response, got %+v", tasks)
	}
}

func TestRequestStateReturnsComputedFrame(t *testing.T) {
	t.Setenv("APPDATA", t.TempDir())
	store, err := config.Load()
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	eng := New(store, nil)

	start := mustTime("2026-06-25T08:00:00+08:00")
	end := mustTime("2026-06-25T18:00:00+08:00")
	tk := makeTask("t1", start, start, end)
	eng.HandleCommand(ipc.Command{Action: "createTask", Task: tk})

	resp := eng.HandleCommand(ipc.Command{Action: "requestState"})
	st, ok := resp.(ipc.State)
	if !ok {
		t.Fatalf("requestState should return ipc.State, got %T", resp)
	}
	if !st.Visible || len(st.Segments) == 0 {
		t.Fatalf("expected visible state with segments, got visible=%v segs=%d", st.Visible, len(st.Segments))
	}
}
