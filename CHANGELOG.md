# Hope 更新日志

## v0.8 (2026-06-26)

### v0.8.9 (2026-06-27)

#### 回退与修复

- **回退 v0.8.8 改动**：撤销了 v0.8.8 中对 reverse 方向填充绘制和统一图片旋转为 0° 的修改，避免进度条与图片位置/长度失调的问题。当前行为回到 v0.8.7 状态。
- **版本号**：更新至 `v0.8.9`。

### v0.8.7 (2026-06-27)

#### 新增与修复

- **四边环绕调试日志**：`engine.writeAllFourDebugLog` 现在同时输出到 `%APPDATA%\Hope\logs\allfour-debug.log` 与 `stderr`（VS Code debug console 可捕获）。
- **调试日志开关**：移除 `HOPE_DEBUG_ALLFOUR` 环境变量限制；四边环绕调试日志仅在勾选"我全都要（四边环绕）"时才会输出。
- **WPF 调试转发**：当 IDE 调试器附加（`Debugger.IsAttached`）时，`HeadlessSupervisor` 会自动为 `hope-headless.exe` 添加 `--debug` 并转发其 stdout/stderr 到 `Debug.WriteLine`，便于在 VS Code debug console 中直接查看 headless 日志。
- **版本号**：更新至 `v0.8.7`。

### 新增功能

#### 1. 进度条位置与方向设置

- **全局设置**：新增进度条位置选择（顶边/底边/左边/右边），默认顶边
- **全局设置**：新增进度条方向选择（水平边：从左到右/从右到左；垂直边：从上到下/从下到上），默认智能设定
- **高级设置**：新增"我全都要（四边环绕）"复选框，勾选后从当前位置出发沿当前方向环绕屏幕四边
- **图片旋转**：四边环绕时，图片随当前活跃边旋转，保持图片始终从对应边向屏幕内侧"长出"
- **详细规格**：详见 `docs/v0.7-新增功能规格.md` §1

#### 2. 庆祝模式（四边闪烁）

- **到期提醒新增模式**：`celebrate`（庆祝模式），与 `keep`/`blink`/`hide` 同级互斥
- **效果**：任务到期时，四边进度条同时闪烁，持续到用户打开设置查看后停止
- **详细规格**：详见 `docs/v0.7-新增功能规格.md` §2

#### 3. 任务级位置覆盖

- **高级设置**：新增"允许任务级位置覆盖"选项
- **功能**：开启后，可为单个任务指定展示位置（顶/底/左/右），覆盖全局设置
- **详细规格**：详见 `docs/v0.7-新增功能规格.md` §3

#### 4. 任务完成按钮

- **任务列表**：新增"完成"按钮（✓）
- **重复任务**：点击后自动修改时间，进入下个周期
- **单次任务**：点击后标记为"已完成"，不再渲染到桌面，但保留在任务列表中
- **详细规格**：详见 `docs/v0.7-新增功能规格.md` §4

#### 5. 颜色去重优化

- **行为变更**：不再限制用户选择相同颜色
- **保存提示**：保存时弹出对话框，提示用户当前颜色已被哪些任务使用，询问是否继续使用
- **详细规格**：详见 `docs/v0.7-新增功能规格.md` §5

### 技术改动

#### Go 端（headless）

- `config.Settings` 新增字段：
  - `BarPosition` (string, 默认 "top")
  - `BarDirection` (string, 默认 "forward")
  - `AllFour` (bool, 默认 false)
  - `ScreenWidth` (float64, 默认 0)
  - `ScreenHeight` (float64, 默认 0)
- `task.Task` 新增字段：
  - `OverridePosition` (string, 默认 "")
  - `CompletedAt` (*time.Time, 默认 nil)
- `task.Task` 新增方法：
  - `Complete(now time.Time)` - 标记任务完成或进入下个循环
- `task.Segment` 新增字段：
  - `ImageRotation` (float64, 默认 0) - 四边环绕时图片旋转角度

#### C# 端（WPF Desktop）

- `Ipc.Models.SettingsDto` 新增属性：
  - `BarPosition` (string)
  - `BarDirection` (string)
  - `AllFour` (bool)
  - `ScreenWidth` (double)
  - `ScreenHeight` (double)
- `Ipc.Models.TaskDto` 新增属性：
  - `OverridePosition` (string)
- `Ipc.Models.Segment` 新增属性：
  - `ImageRotation` (double)
- `Views.ConfigWindow` 新增 UI：
  - 全局设置：进度条位置下拉、进度条方向下拉
  - 高级设置：我全都要（四边环绕）复选框、允许任务级位置覆盖复选框
  - 任务编辑区：展示位置下拉（当允许任务级位置覆盖开启时）
  - 任务列表：完成按钮
- `Overlay.OverlayWindow` 改造：
  - 支持多实例（List<OverlayWindow>）
  - 支持位置、方向和图片旋转
  - 支持四边环绕（创建4个窗口）
  - 支持庆祝模式（四边同时闪烁）

### 配置兼容性

- 旧版 `config.json` 缺少新字段时，自动使用默认值（mergeSettings 兼容处理）
- 旧版 `config.json` 中 `BarPosition = "allFour"` 会迁移为 `BarPosition = "top"` + `AllFour = true`
- 旧版 `tasks.json` 缺少新字段时，自动使用默认值

### 验收标准（新增）

| # | 场景 | 期望结果 | 状态 |
|---|--------|----------|------|
| 14 | 用户选择"底边" | 进度条显示在屏幕底边 | ✅ |
| 15 | 用户选择"左边" | 进度条显示在屏幕左边，从上到下填充 | ✅ |
| 16 | 用户启用"我全都要（四边环绕）" | 从当前位置沿当前方向环绕四边，图片随边旋转 | ✅ |
| 17 | 任务到期且 `behaviors` 含 `celebrate` | 四边同时闪烁 | ✅ |
| 18 | 用户点击"完成"按钮（重复任务） | 任务时间自动更新到下个周期 | ✅ |
| 19 | 用户点击"完成"按钮（单次任务） | 任务标记为已完成，不再渲染 | ✅ |
| 20 | 用户保存任务（颜色重复） | 弹出对话框提示，用户可选择继续 | ✅ |

---

## v0.6 (2026-06-25) - 已完成

### 新增功能

- **到期提醒「使用全局默认」并入下拉**：选中时 `task.expiredBehaviors = null`，运行期完全沿用全局
- **循环任务**：
  - 支持每天 / 每 N 天 / 按星期
  - 时分定义每日窗口，支持跨午夜
  - 窗口结束套用到期提醒并在下次开始重置

### 技术改动

- `task.Task` 新增 `Recurrence` 字段
- `task.BuildLayout` 支持循环任务的时间窗口计算
- 配置窗预览支持循环任务的进度计算

---

## v0.5 (2026-06-24) - 已完成

### 新增功能

- **到期提醒升级**：多选互斥（保持/隐藏/闪烁 + 通知）
- **全局默认 + 任务级覆盖**：任务编辑区「到期提醒」单行，新建任务自动同步全局默认
- **`blink` 改柔和 alpha 渐变**：正弦缓动淡出/淡入，持续到查看
- **`keep` 保留满色段**：到期后 100% 满色段常驻顶栏

### 技术改动

- `expiredBehavior` (string) 改为 `expiredBehaviors` ([]string)
- 配置窗下拉改为「显示模式单选 + 通知独立勾选」

---

## v0.4 (2026-06-24) - 已完成

### 新增功能

- **设置 UI 重构**：刷新间隔、条高、自启、运行时显示等修改即生效
- **配置窗交互增强**：取色盘、日期时间选择器、快填、实时预览
- **WPF-UI 主题**：FluentWindow + Mica/Acrylic、亮暗跟随系统
- **品牌图标**：应用图标、托盘小图（按主题着黑/白）
- **窗高自适应**：按任务编辑 Tab 内容动态计算窗体高度
- **双向互拉**：Desktop 拉起 Headless、Headless 拉起 Desktop

### 技术改动

- 引入 WPF-UI 4.2.0 (NuGet, MIT)
- 移除「保存设置」按钮，全局设置修改即 `updateSettings`
- `imageMaxSize` 字段：控制跟随图片的最大高度（15-30px）

---

## v0.3 (2026-06-23) - 已完成

### 新增功能

- **DWM 分段顶栏 Overlay**：多色拼接、透明未完成区、点击穿透
- **跟随图片/动图**：进度条下方、随 `fillEnd` 移动、GIF 循环播放
- **悬停展示任务名 + 倒计时**

### 技术改动

- `Overlay/OverlayWindow.xaml.cs`：Win32 扩展样式、WM_NCHITTEST 穿透
- `Overlay/ImageSprite.cs`：GDI+ 图片加载、GIF 帧动画

---

## v0.2 (2026-06-22) - 已完成

### 新增功能

- **WPF 配置窗体**：任务 CRUD、全局设置
- **系统托盘**：打开设置、暂停/继续、隐藏/显示、关于、退出
- **IPC 命名管道**：`\\.\pipe\Hope\progress`

### 技术改动

- `Views/ConfigWindow.xaml.cs`：双 Tab、DataGrid 任务列表
- `Ipc/IpcClient.cs` + `Ipc/Models.cs`：JSON Lines 协议

---

## v0.1 (2026-06-20) - 已完成

### 新增功能

- **Headless 核心（Go）**：任务计时、百分比计算、配置持久化
- **IPC 广播**：每秒广播状态到命名管道
- **单实例**：Go `Global\HopeHeadless` Mutex

### 技术改动

- `src/headless/`：engine、task、ipc 包
- `go build -ldflags="-s -w -H=windowsgui"`

---

_更新日志结束_
