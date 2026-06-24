# Hope（盼头）产品与技术方案

> 版本：v0.4  
> 更新说明：根据仓库实际代码同步 **实现状态**；标注 Phase 1 已完成 / 部分完成 / 未做项。

**图例：** ✅ 已实现并合入代码　⚠️ 后端或局部已有，UI/联调未完成　❌ 未实现（含 Phase 2）

---

## 0. 实现状态（截至 2026-06-24）

> 对照 `src/headless`、`src/win-desktop`、`.github/workflows/release.yml`、`setup.iss`。

### 0.1 Phase 1 总览

| 模块 | 状态 | 代码位置 / 备注 |
|------|------|-----------------|
| Headless 核心（Go） | ✅ | `src/headless/`：`engine`、`task`、`config`、`ipc` |
| 墙钟实时进度 & 多任务分段 | ✅ | `task.BuildLayout`、`task.Percent`；单测见 `task_test.go` |
| 配置持久化（JSON） | ✅ | `%APPDATA%\Hope\config.json` + `tasks.json`；含 UTF-8 BOM 剥离 |
| IPC 命名管道广播 & 命令 | ✅ | `\\.\pipe\Hope\progress`；`hide`/`show` 已实现（文档原仅列 pause/resume） |
| Headless 单实例 | ✅ | `Global\HopeHeadless` Mutex |
| Headless 日志 | ✅ | `logs/hope-headless.log`；`--debug` 额外输出控制台 |
| WPF 配置窗体 & 任务 CRUD | ✅ | `Views/ConfigWindow`：名称、颜色、类型、起止时间、跟随图片 |
| 系统托盘 | ✅ | `App.xaml.cs`：打开设置、暂停/继续、隐藏/显示、关于、退出 |
| DWM 分段顶栏 Overlay | ✅ | `Overlay/OverlayWindow`：多色填充、透明未完成区、固定条高 |
| 点击穿透 & 不出 Alt+Tab | ✅ | `NativeMethods` + `WM_NCHITTEST` → `HTTRANSPARENT` |
| 悬停展示任务名 | ✅ | 全局光标轮询；仅进度条高度带内 **已填充段** 响应 |
| 跟随图片/动图 | ✅ | `Overlay/ImageSprite`：任意格式加载、>15px 等比缩放、GIF 逐帧播放 |
| 截止后行为 `expiredBehavior` | ⚠️ | Headless 广播 `expired[]`；Desktop 已实现 `notify`（托盘气球）、`blink`（顶栏闪烁）；**配置窗体尚无设置项**，默认 `keep` |
| 用户设置 UI（条高、刷新间隔等） | ⚠️ | `updateSettings` IPC 与 `config.Settings` 已有；**Desktop 未读回设置、配置窗无设置页**，条高暂硬编码 4px |
| Desktop → Headless 互拉 | ✅ | `HeadlessSupervisor`：检测进程缺失则拉起 |
| Headless → Desktop 互拉 | ⚠️ | `main.go --desktop` 已实现；**Desktop 拉起 Headless 时未传 `--desktop`**，该方向互拉需安装包或手动带参启动 |
| Desktop 单实例 | ✅ | `Global\HopeDesktop` Mutex |
| CI 编译 & 单测 | ✅ | `.github/workflows/release.yml`：`go test` + `dotnet publish` |
| Inno Setup 安装包 | ✅ | `setup.iss`（含可选开机自启任务）；需打 tag `v*` 触发 Release 上传 |
| VS Code 调试配置 | ✅ | `.vscode/launch.json` + `tasks.json`（WPF 用官方 VS Code + `ms-dotnettools.csharp`） |
| Phase 2 全屏插件 | ❌ | `src/plugins/fullscreen/` 仅占位 README |

**当前可交付边界：** 本地开发与日常使用闭环已通；距 v1.0 尚差设置 UI、双向互拉联调、帮助文档与验收清单人工回归。

### 0.2 验收标准对照（§9）

| # | 场景 | 状态 |
|---|------|------|
| 1 | 多任务多色拼接 | ✅ |
| 2 | 点击穿透 | ✅ |
| 3 | 不出 Alt+Tab / Win+Tab | ✅ |
| 4 | 悬停展示任务名 | ✅（仅已填充段） |
| 5 | 墙钟 90% 示例 | ✅（单测覆盖） |
| 6 | 无边框全屏可见 | ⚠️ 未自动化测；设计支持，需人工验证 |
| 7 | 托盘隐藏顶栏 | ✅ |
| 8 | 托盘退出 & 互拉停止 | ✅ |
| 9 | 单实例 | ✅ |
| 10 | 无任务不显示 | ✅ |
| 11 | 到期 `notify` | ✅（需 `expiredBehavior=notify`，当前仅能通过改 `config.json`） |
| 12 | Headless 崩溃拉起 | ✅（Desktop 侧） |
| 13 | 暂停不冻结墙钟 | ✅ |

---

## 1. 项目简介

| 项 | 内容 |
|---|---|
| 项目名称 | Hope（中文名：盼头） |
| 目标平台 | Windows 10 / Windows 11（首版）；macOS / Linux 预留扩展位 |
| 核心功能 | 桌面效率提示工具：用户为多个任务设定时间与颜色，屏幕顶端显示一根 `1~10px` 高的全宽**分段彩色**进度条，各任务按自身进度在对应色段内填充 |
| 核心场景 | 办公、浏览、窗口化/无边框应用、非独占全屏游戏 |
| 延伸场景 | 独占全屏游戏覆盖（通过可选插件实现，见第 6 节） |

### 1.1 核心痛点（修订）

- 用户沉浸工作或娱乐时容易忘记截止时间。
- 进度条必须：**置顶可见、点击穿透（不抢焦点、不误触）、不参与 Alt+Tab / Win+Tab**；唯一交互为悬停展示任务名。
- 独占全屏游戏覆盖是**高价值延伸场景**，技术风险与反作弊约束高，**不作为首版交付范围**。

### 1.2 产品定位（待你确认）

- [ ] 目标用户画像：__________（例：备考学生 / 自由职业者 / 防沉迷家长自用）
- [ ] 与番茄钟、Focus 类工具的差异：__________
- [ ] 是否面向公开发布 / 仅自用：__________

---

## 2. 分阶段交付策略

### Phase 1：核心包（首版必做）

覆盖 **大部分办公场景** 与 **非独占全屏游戏**（无边框全屏、窗口化、Win10+ 全屏优化下的「全屏」）。

| 模块 | 说明 | 状态 |
|------|------|------|
| Headless 核心 | Go 后台：任务计时、百分比计算、配置持久化、IPC 广播 | ✅ |
| 配置窗口 | WPF：创建/编辑任务、截止时间、基础设置 | ⚠️ 任务 CRUD ✅；全局设置 UI ❌ |
| 系统托盘 | 托盘图标入口：打开设置、暂停/恢复、退出 | ✅ |
| DWM 覆盖层 | 透明置顶、鼠标穿透的顶栏进度条窗口 | ✅ |
| 跟随图片/动图 | 进度条下方、随 `fillEnd` 移动 | ✅ |

**首版不做的内容：**

- 独占全屏游戏 Present Hook
- Xbox Game Bar Widget
- 游戏兼容数据库
- MSIX / 证书旁加载安装链

### Phase 2：全屏游戏扩展包（插件）

以 **可选插件 / 拓展包** 形式交付，独立安装、独立版本号、独立风险告知。

| 模块 | 说明 |
|------|------|
| 插件宿主协议 | 核心包暴露稳定 IPC + 插件加载接口 |
| `hope-plugin-fullscreen` | DXGI Present Hook 顶栏绘制（或后续 Game Bar 等方案） |
| 兼容分级 | 绿 / 黄 / 红游戏库 + 反作弊提示 |
| 显示模式助手（可选） | 自动引导或强制无边框（插件子模块） |

**插件与核心包关系：**

- 核心包可独立运行；未安装插件时，独占全屏场景下顶栏可能不可见（Phase 1 **不自动检测、不弹窗提示**，见 §7.6）。
- 插件只增强「呈现层」，不替代 Headless 计时逻辑。

---

## 3. 总体架构

### 3.1 Phase 1 架构图

```
┌─────────────────────────────────────────────────────────────┐
│                     Hope 核心包 (Phase 1)                    │
├─────────────────────────────────────────────────────────────┤
│  [hope-desktop.exe]                                          │
│    ├ 配置窗体  ←──IPC 命令──→  [hope-headless.exe]           │
│    ├ 系统托盘                         │                      │
│    └ DWM 分段顶栏 Overlay  ←──IPC 广播──┘                    │
│         点击穿透 / 不出现在 Alt+Tab / 悬停显示任务名          │
└─────────────────────────────────────────────────────────────┘
```

### 3.2 含插件时的架构图（Phase 2）

```
[Go Headless 核心]
       │ IPC（统一数据契约）
       ├──────────────────→ [DWM 覆盖层]     ← Phase 1，始终存在
       │
       └──────────────────→ [插件：全屏 Hook]  ← Phase 2，可选
                              或未来其他呈现插件
```

### 3.3 进程模型（Phase 1，已确定）

| 进程 | 技术栈 | 职责 |
|------|--------|------|
| `hope-headless.exe` | Go | 任务与进度实时计算、配置持久化、IPC Server、监视并拉起 Desktop |
| `hope-desktop.exe` | C# WPF | 配置 UI + 系统托盘 + DWM 分段顶栏 Overlay、监视并拉起 Headless |

**已决：2 进程架构**——`hope-desktop.exe` 同时承载托盘、配置窗体与 Overlay 窗口。

**进程互保（Watchdog）：**

- Headless 退出异常时，Desktop 在数秒内重新拉起 Headless。✅ `HeadlessSupervisor`
- Desktop 退出异常时，Headless 在数秒内重新拉起 Desktop（Headless 需以 `--desktop <path>` 启动）。⚠️ 代码已有，**Desktop 默认拉起未带该参数**
- 用户托盘「退出」时，双方约定正常关闭，互拉逻辑不触发。✅

---

## 4. 技术选型

### 4.1 已确定

| 领域 | 选型 | 理由 | 状态 |
|------|------|------|------|
| 后台核心 | **Go** | 单文件分发、低内存、适合无 UI 常驻 | ✅ |
| 配置与托盘 | **C# WPF** | Win32 生态成熟、开发效率高 | ✅ |
| Phase 1 覆盖层 | **DWM 透明置顶窗（WPF）** | 不注入；单条顶栏多色分段；见 §5.4 | ✅ |
| 鼠标穿透与窗口行为 | Win32 扩展样式 + `WM_NCHITTEST` | 点击穿透、不可聚焦、不出现在 Alt+Tab / Win+Tab | ✅ |
| 日志 | Go `slog` + 滚动文件 | 路径见 §4.4；支持用户导出 | ⚠️ 写文件 ✅；UI 导出 ❌ |
| IPC 传输 | **命名管道**（JSON Lines） | 首版足够；高频场景后续可加共享内存 | ✅ |
| 单实例 | Go `Global\HopeHeadless`；Desktop `Global\HopeDesktop` | 防止多开 | ✅ |
| Phase 2 插件协议 | **独立 exe + 同一 IPC 契约** | 隔离崩溃与反作弊风险 | ❌ 仅占位 |
| 配置存储 | 本地 JSON 文件 | 简单、可备份；路径见 4.4 | ✅ |
| 首版安装包 | **Inno Setup** | 仅打包 exe，无 MSIX 证书链 | ✅ 脚本就绪 |
| 仓库结构 | **Monorepo** | 统一 CI、统一版本 | ✅ |

### 4.2 待选型（剩余）

| 领域 | 选项 | 备注 |
|------|------|------|
| Overlay 备选实现 | Win32 + Direct2D | 仅当 WPF 透明窗闪烁/穿透异常时启用 |
| 日志级别 | `info` / `debug` | 默认 `info`；`--debug` 可开控制台（见 §11） |

### 4.3 Phase 2 插件技术方向（预研，非首版）

| 方案 | 适用 | 风险 | 建议 |
|------|------|------|------|
| DXGI Present Hook | 真独占全屏 | 反作弊、多 overlay 冲突 | 插件主推方向 |
| 显示模式助手 | 可改无边框的游戏 | 低 | 作为插件子模块，优先尝试 |
| Xbox Game Bar Widget | 部分无边框/FSO | 需 Pin、独占全屏差 | 备选，非默认 |
| DWM 顶层窗 | 非独占场景 | 极低 | 已在核心包实现 |

### 4.4 配置与数据路径（默认值，可改）

```
%APPDATA%\Hope\
├── config.json          # 用户设置
├── tasks.json           # 任务列表（或与 config 合并，待定义）
└── logs\
    └── hope-headless.log
```

---

## 5. Phase 1 实现规范

### 5.1 Headless 核心（Go）✅

**职责：**

- [x] 读取/写入本地配置与多任务列表
- [x] 按 **墙钟实时** 计算各任务 `percent` 与顶栏 `segments`（规则见 §7.2，**不累加、不因休眠暂停**）
- [x] 以固定间隔（默认 **1 秒**，`settings.refreshSec` 可配置）向 IPC 订阅方广播状态
- [x] 接收来自 Desktop 的控制命令（任务 CRUD、暂停、隐藏、退出等）
- [x] 监视 `hope-desktop.exe`（`--desktop` 参数提供时）；异常退出时重新拉起
- [x] 启动时不显示控制台窗口（`--debug` 时除外）
- [ ] 二次启动时唤醒已有 Desktop 窗口（可选，未实现）

**进度计算原则：**

- 一律基于 `time.Now()` 与任务起止时刻 **实时推导**，不维护「已累计秒数」。
- 休眠期间任务在时间上仍在进行，唤醒后进度与「未休眠」一致。
- 例：任务 08:00–18:00，17:00 唤醒 → 各任务 percent 与连续运行到 17:00 相同（整体约 90% 处）。

**编译：**

```bash
go build -ldflags="-s -w -H=windowsgui" -o hope-headless.exe .
```

**单实例：**

- [x] 使用 Windows Mutex `Global\HopeHeadless`；二次启动时直接退出（唤醒 Desktop 待做）

### 5.2 IPC 契约（Phase 1）✅

**管道名：** `\\.\pipe\Hope\progress`

> 首版订阅方为 Desktop；Phase 2 插件只读订阅同一广播。

**服务端 → 客户端（广播，JSON Lines，每秒）：**

```json
{
  "version": 1,
  "visible": true,
  "state": "running",
  "timelineStart": "2026-06-23T08:00:00+08:00",
  "timelineEnd": "2026-06-23T18:00:00+08:00",
  "segments": [
    {
      "taskId": "uuid-1",
      "name": "晨会",
      "color": "#E53935",
      "gif": "C:\\Users\\me\\AppData\\Roaming\\Hope\\gifs\\cat.gif",
      "barStart": 0.0,
      "barEnd": 30.0,
      "percent": 30.0,
      "fillEnd": 9.0
    },
    {
      "taskId": "uuid-2",
      "name": "写文档",
      "color": "#43A047",
      "barStart": 30.0,
      "barEnd": 60.0,
      "percent": 60.0,
      "fillEnd": 48.0
    },
    {
      "taskId": "uuid-3",
      "name": "复盘",
      "color": "#FDD835",
      "barStart": 60.0,
      "barEnd": 100.0,
      "percent": 90.0,
      "fillEnd": 96.0
    }
  ]
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `version` | int | 协议版本 |
| `visible` | bool | 是否显示顶栏（无任务时为 `false`） |
| `state` | string | `idle` / `running` / `paused` / `expired`；`paused` 时 `visible=false`，但 `segments` 仍按墙钟更新 |
| `timelineStart` | string (RFC3339) | 所有的任务中最早有效开始时刻 |
| `timelineEnd` | string (RFC3339) | 所有任务中最晚截止时刻 |
| `segments[]` | array | 按时间顺序拼接的色段，**同一物理顶栏** |
| `segments[].color` | string | 用户为任务指定的颜色（`#RRGGBB`） |
| `segments[].gif` | string | 可选；任务的本地**图片/动图**路径（任意图片格式），挂在进度条**下方**、跟随该段 `fillEnd` 移动；动图循环播放 |
| `segments[].barStart` / `barEnd` | float | 该任务在整条顶栏上的占比区间（0–100） |
| `segments[].percent` | float | 该任务自身进度 0–100 |
| `segments[].fillEnd` | float | 该色段在顶栏上的实际填充右边界（`barStart + (barEnd-barStart)*percent/100`） |

**分段布局规则（已确定）：**

1. 仅包含 **未过期** 的任务；全局 `state=paused` 时仍按墙钟计算 `percent`，但不向 Overlay 绘制（`visible=false`）。
2. `timelineStart` = 所有纳入任务的有效 `startAt` 之最小值；`timelineEnd` = 所有 `endAt` 之最大值。
3. 每任务占 `[barStart, barEnd]`，宽度 ∝ `(endAt - startAt) / (timelineEnd - timelineStart)`。
4. 段内填充：自 `barStart` 向右填充至 `fillEnd`，颜色为任务色；段间无间隙，多色拼接。
5. **未完成部分不绘制任何底色**：超过 `fillEnd` 的区域完全透明、不可点击、悬停不交互（只有已填充的彩色部分才响应悬停）。
6. 视觉示例：`[==红 30%==][====绿 60%====][======黄 90%======]`（数字为各任务自身 percent）。

**单任务 percent（已确定）：**

| 类型 | 有效开始 `startAt` | 公式 |
|------|-------------------|------|
| **定时任务** | 用户填写 | `percent = clamp((now - startAt) / (endAt - startAt) * 100, 0, 100)` |
| **即时任务** | 创建时刻 `createdAt` | 同上，`startAt = createdAt` |

**客户端 → 服务端（命令，JSON）：**

```json
{"action":"createTask","task":{"id":"uuid","name":"写文档","type":"scheduled","color":"#43A047","startAt":"2026-06-23T10:00:00+08:00","endAt":"2026-06-23T18:00:00+08:00"}}
```

```json
{"action":"createTask","task":{"id":"uuid","name":"买菜","type":"instant","color":"#E53935","endAt":"2026-06-23T20:00:00+08:00"}}
```

```json
{"action":"updateTask","task":{...}}
```

```json
{"action":"deleteTask","taskId":"uuid"}
```

```json
{"action":"listTasks"}
```

```json
{"action":"updateSettings","settings":{"barHeightPx":4,"expiredBehavior":"notify"}}
```

```json
{"action":"pause"}
```

```json
{"action":"resume"}
```

```json
{"action":"hide"}
```

```json
{"action":"show"}
```

```json
{"action":"quit"}
```

> 广播额外字段 `expired[]`（任务刚到期时一次性下发，供 Desktop 执行 `expiredBehavior`）。✅ 已实现

### 5.3 WPF 配置窗体 + 系统托盘 ⚠️

**配置窗体：**

- [x] 任务字段：**名称**、**颜色**（必填）、**类型**（`scheduled` / `instant`）、**截止时间**；定时任务另填 **开始时间**；可选 **跟随图片/动图**（任意图片格式，文件选择）
- [x] 支持多任务列表：新建 / 编辑 / 删除
- [x] 保存后通过 IPC 同步至 Headless
- [x] 关闭窗口时 **最小化到托盘**，不退出进程
- [ ] 全局设置页：进度条高度、`expiredBehavior`、刷新间隔、开机自启（后端已支持，UI 未做）

**进程互保：**

- [x] Desktop 监视 Headless（见 §3.3）
- [ ] Headless 监视 Desktop（需安装包或启动参数 `--desktop`）
- [x] 托盘「退出」：Desktop 先发 `quit`，双方正常结束，禁用互拉

**托盘菜单：**

| 菜单项 | 行为 | 状态 |
|--------|------|------|
| 打开设置 | 显示配置窗体 | ✅ |
| 暂停 / 继续 | 隐藏顶栏；**不冻结墙钟** | ✅ |
| 显示进度条 / 隐藏 | 设置 `visible`（`hide`/`show`） | ✅ |
| 关于 | 版本号、独占全屏说明、插件入口 | ✅ |
| 退出 | `quit`，结束所有进程 | ✅ |

**托盘图标：**

- [ ] 常态 / 暂停 / 即将到期 是否区分图标：__________（当前为系统默认图标）
- [ ] 气球通知规则：__________（`expiredBehavior=notify` 时已有一次性气球 ✅）

### 5.4 DWM 覆盖层（分段顶栏）✅

**窗口特性：**

- [x] 无边框、无标题栏；宽度 = 主屏宽度；进度条高度 `1~10px`（可配置，默认 `4px`；**Desktop 暂未从设置读回，硬编码 4**）
- [x] 置顶：`HWND_TOPMOST` + WPF `Topmost=True`
- [x] **不参与任务切换：** `WS_EX_TOOLWINDOW`，不出现在 Alt+Tab / Win+Tab 列表
- [x] **不可聚焦：** `WS_EX_NOACTIVATE`；`ShowActivated=false`；不抢夺前台焦点
- [x] **点击穿透：** `WS_EX_LAYERED` + `WM_NCHITTEST` → `HTTRANSPARENT`（点击与拖拽均落到下层窗口）
- [x] 背景透明；仅绘制 §5.2 所述多色拼接色带（**仅已填充部分**）
- [x] 多显示器：首版 **仅主屏**（与 §7.4 一致）

**唯一交互：悬停展示任务名** ✅

- [x] 点击仍穿透；光标在顶栏 **已填充段** 内时展示 Tooltip（任务名 + percent）
- [x] 实现：`WM_NCHITTEST` 穿透 + **全局鼠标位置轮询**（`GetCursorPos`，150ms）；未使用低级钩子
- [x] 未完成部分、图片区：不响应悬停

**Win32 扩展样式（经 `WindowInteropHelper` 设置）：**

```
WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
```

点击穿透通过 `WM_NCHITTEST` → `HTTRANSPARENT`，**不要**长期开启 `WS_EX_TRANSPARENT`（否则无法做悬停检测）。

**绘制：** ✅

- [x] 按 IPC `segments` 从左至右绘制各色段**已填充部分**（`barStart` → `fillEnd`）
- [x] **不绘制未完成部分的底色**：`fillEnd` 之后透明，不可点击、悬停不交互
- [x] 进度条**粗细恒为 `barHeightPx`（1~10px）**，不随是否带图片而变化
- [x] 过期 `expiredBehavior=blink`：顶栏闪烁动画（`OverlayWindow.Blink`）
- [x] **跟随图片/动图：** 含 `gif` 的段在进度条**下方**渲染（`ImageSprite`），水平中心对齐 `fillEnd`、随进度移动
  - [x] 支持常见图片格式（GIF / PNG / JPG / BMP / WebP / TIFF 等）；**动画 WebP / APNG 仅首帧**（`System.Drawing` 限制）
  - [x] 图片高度 **超过 15px 时等比缩放到 15px**，不超过则保持原始尺寸
  - [x] 存在图片时窗口向下扩展（高度 = 条高 + 图片区 ≤15px）；图片区点击穿透

**适用边界（写入「关于」）：**

- ✅ 桌面、浏览器、Office、IDE
- ✅ 无边框全屏、窗口化全屏游戏
- ✅ 开启全屏优化（FSO）的「全屏」游戏
- ❌ 真·独占全屏（需 Phase 2 插件；**首版不检测、不提示**）

### 5.5 目录结构（Monorepo）✅

```
Hope/
├── .github/workflows/release.yml   # ✅ Go + WPF + Inno Setup
├── .vscode/                        # ✅ launch.json / tasks.json（VS Code 调试）
├── src/
│   ├── headless/                   # ✅ Go 核心
│   │   ├── main.go
│   │   ├── engine/engine.go
│   │   ├── ipc/ipc.go
│   │   ├── task/task.go, task_test.go
│   │   ├── config/config.go
│   │   └── singleinstance_windows.go
│   ├── win-desktop/                # ✅ WPF
│   │   ├── Hope.Desktop.csproj
│   │   ├── App.xaml(.cs)
│   │   ├── HeadlessSupervisor.cs
│   │   ├── Views/ConfigWindow.*
│   │   ├── Ipc/IpcClient.cs, Models.cs
│   │   ├── Overlay/OverlayWindow.*, ImageSprite.cs
│   │   └── Interop/NativeMethods.cs
│   └── plugins/fullscreen/         # ❌ Phase 2 占位 README
├── docs/plugin-ipc.md
├── setup.iss                       # ✅
├── Hope-产品与技术方案.md
└── README.md
```

---

## 6. Phase 2：全屏游戏插件（拓展包）

### 6.1 交付形态

| 项 | 建议 |
|----|------|
| 安装包名 | `Hope.Plugin.Fullscreen_Setup.exe` |
| 与核心包关系 | 可选安装；检测核心包是否已装 |
| 许可与风险提示 | 安装前强制确认反作弊与兼容性说明 |
| 更新节奏 | 插件版本独立于核心包 |

### 6.2 插件职责边界

**插件负责：**

- 检测前台全屏游戏进程
- 在 Present Hook 路径绘制顶栏（或备选方案）
- 向用户展示兼容性与降级建议

**插件不负责：**

- 任务 CRUD、计时算法（仍由 Headless 负责）
- 替换核心包 DWM 覆盖层

**双呈现器协调（Phase 2）：**

- 插件 Hook 激活且绘制成功时，Desktop 侧 DWM 顶栏 **隐藏**，避免双线重叠
- 协调字段（预留）：广播中 `presenter: "dwm" | "hook"`

### 6.3 插件 IPC

- 订阅与 Desktop 相同的 Headless 广播管道
- 额外命令（预留）：`registerPresenter`、`reportCompatibility`

### 6.4 插件预研任务清单

- [ ] DXGI Present Hook POC（DX11 单后端）
- [ ] Top 50 Steam 游戏呈现模式分布
- [ ] EAC / BattlEye / Vanguard 下行为记录（不承诺零风险）
- [ ] 与 RTSS / Steam Overlay 共存策略
- [ ] 代码签名方案

---

## 7. 产品待补充内容

> 以下条目请直接删改、勾选或填空。

### 7.1 用户与场景

- [ ] **主要用户是谁？** __________
- [ ] **次要场景优先级：** 办公 / 学习 / 轻量游戏 / 重度电竞 —— 排序：__________
- [ ] **是否强调「防沉迷」叙事？** 是 / 否 / 仅插件文案体现

### 7.2 任务与进度条语义（已确定）

**多任务 + 单色段拼接（已确定）：**

- 支持多个任务同时进行；**物理上仅一根顶栏**
- 每个任务必须指定 **独立颜色**（`#RRGGBB`）
- 各任务按时间跨度映射到顶栏上的 **[barStart, barEnd]** 区间，段内按该任务 `percent` 填充；多色从左到右拼接
- 布局与 IPC 字段见 §5.2

**跟随图片 / 动图（已确定）：** ✅

- 每个任务可选配置一张本地图片；图片挂在进度条 **下方**，水平位置跟随该任务进度前沿（`fillEnd`）移动
- **支持常见图片格式**（GIF / PNG / JPG / BMP / WebP / TIFF 等）；多帧动图（动画 GIF）由 Overlay 逐帧循环播放（`ImageAnimator` 驱动，约 15fps）
- **尺寸规则：** 图片高度超过 15px 时等比缩放到 15px；不超过则保持原始尺寸
- **进度条本身粗细不受图片影响**，恒为 `barHeightPx`；图片在进度条下方独立成区
- 图片不影响计时与穿透；文件缺失或损坏时静默跳过
- IPC / 任务字段名仍为 `gif`（历史命名），语义为任意图片路径

**任务类型与 percent（已确定）：**

| 类型 | 字段 | percent |
|------|------|---------|
| **定时任务** `scheduled` | `startAt` + `endAt` + `color` | `(now - startAt) / (endAt - startAt) * 100`，clamp 0–100 |
| **即时任务** `instant` | `endAt` + `color`；`startAt` 取 `createdAt` | 同上 |

**时间语义（已确定）：**

- 进度 **一律墙钟实时计算**，不累加有效工作时间
- **休眠不改变公式**：休眠期间时间轴照常推进，唤醒后与未休眠使用同一 `now`
- **全局暂停不冻结墙钟**：暂停期间 Headless 仍按 `time.Now()` 维护各任务 `percent`；仅隐藏顶栏（`visible=false`）。恢复后顶栏立刻显示暂停期间已推进的进度
- 例：08:00–18:00 任务，17:00 打开电脑 → 该任务 `percent = 90%`

**截止后行为（已确定）：** ⚠️ 逻辑已实现，配置 UI 未做

- 用户在以下选项中 **单选（互斥）**，禁止多选：
  - `keep` 保持 — ✅
  - `blink` 闪烁 — ✅ Overlay 闪烁
  - `notify` 系统通知 — ✅ 托盘气球
  - `hide` 自动隐藏 — ✅ 到期后移出活跃段
- 配置字段：`expiredBehavior`，默认 `keep`（可手动改 `config.json`）

**无任务时（已确定）：** 不创建 / 不显示顶栏窗口（`visible = false`）

### 7.3 提醒与通知

- [ ] 提前提醒节点：__________（例：30 分钟、10 分钟、5 分钟）
- [ ] 提醒方式：顶栏变色 / 托盘 / Windows Toast / 声音
- [ ] 免打扰时段：__________

### 7.4 设置项（首版范围建议）

| 设置 | 默认 | 是否首版 | 实现状态 |
|------|------|----------|----------|
| 进度条高度 (1–10px) | 4px | 是 | ⚠️ `config.json` + IPC ✅；Overlay 暂硬编码 4px |
| 每任务颜色 | 用户必填 | 是 | ✅ |
| 跟随图片/动图 | 可选 | 是 | ✅ |
| 显示显示器 | 主屏 | 是 | ✅ 仅主屏 |
| 截止后行为 `expiredBehavior` | `keep` | 是 | ⚠️ 逻辑 ✅；配置 UI ❌ |
| 刷新间隔 `refreshSec` | 1s | 是 | ✅ Headless；UI 不可调 |
| 开机自启 | 关 | [ ] | ⚠️ `setup.iss` 安装任务 ✅；应用内设置 ❌ |
| 语言 | 简体中文 | [ ] | ❌ 字段预留，未做 i18n |

### 7.5 首次使用引导（Onboarding）

- [ ] 是否需要：是 / 否
- [ ] 步骤草案：安装 → 设第一个 Deadline → 展示顶栏预览 → 说明独占全屏限制与插件入口

### 7.6 异常与降级（已确定）

| 场景 | 期望行为 | 状态 |
|------|----------|------|
| Headless 崩溃 | Desktop 重新拉起 Headless | ✅ |
| Desktop 崩溃 | Headless 重新拉起 Desktop | ⚠️ 需 `--desktop` |
| Overlay 被挡 / 不可见 | **Phase 1 不检测、不提示** | ✅（未做检测） |
| 独占全屏且未装插件 | **Phase 1 不检测、不提示** | ✅（未做检测） |
| 系统休眠 / 唤醒 | 唤醒后按 `time.Now()` 立即重算 | ✅ |
| 用户修改系统时间 | 按新系统时间立即重算 | ✅ |

### 7.7 非功能需求

- [x] 性能目标：CPU < 10%，内存 < 100MB（未正式压测，架构上满足）
- [x] 可靠性：Headless ↔ Desktop 互相拉起（§3.3、§7.6）—— ⚠️ 双向互拉未完全接线
- [ ] 日志：用户可导出（写文件 ✅，导出 UI ❌）
- [ ] 卸载：完全清理 `%APPDATA%\Hope`（安装包卸载行为待验）
- [ ] 安装包小于 30MB（CI 产物待实测）

### 7.8 商业化与合规

- [x] 定价：免费 
- [x] 隐私：纯本地
- [x] 开源协议：MIT

### 7.9 成功指标

- [ ] 安装后 24h 内完成首次任务设置率：___%
- [ ] 7 日留存：___%
- [ ] 「看不到进度条」类反馈占比：< ___%

### 7.10 版本路线图（草案）

| 版本 | 范围 | 状态 |
|------|------|------|
| v0.1 | Headless + IPC + 命令行验证 | ✅ |
| v0.2 | WPF 配置 + 托盘 | ✅ |
| v0.3 | DWM 分段顶栏 + 多任务闭环 + 跟随图片 | ✅ **当前代码基线** |
| v0.4 | 设置 UI、双向互拉、条高读回、`expiredBehavior` 配置 | 🔲 下一步 |
| v1.0 | 安装包验收、帮助文档、图标与 Onboarding | 🔲 |
| v1.x-plugin | 全屏游戏拓展包（独立发版） | ❌ |

---

## 8. CI/CD（Phase 1 简化版）✅

**`release.yml` 步骤：**

1. [x] Checkout
2. [x] Setup Go、.NET SDK
3. [x] 编译 `hope-headless.exe`
4. [x] `go test ./...`
5. [x] 编译 `hope-desktop`（`dotnet publish`）
6. [x] Inno Setup 打 `Hope_Setup.exe`
7. [x] GitHub Release 上传（push tag `v*` 时）

**首版不需要：**

- UWP / MSIX 签名
- 自签名证书注入脚本
- `Add-AppDevPackage.ps1`

---

## 9. 验收标准（Phase 1）

> 逐条实现状态见 **§0.2**。

| # | Given | When | Then | 状态 |
|---|--------|------|------|------|
| 1 | 已安装核心包 | 用户创建 3 个不同颜色任务并保存 | 顶栏出现多色拼接段，各段按自身 percent 填充 | ✅ |
| 2 | 进度条显示中 | 鼠标点击顶栏区域 | 点击落到下层窗口，Overlay 不获焦 | ✅ |
| 3 | 进度条显示中 | Alt+Tab / Win+Tab | 列表中不出现 Hope 顶栏窗口 | ✅ |
| 4 | 进度条显示中 | 鼠标悬停顶栏已填充段 | 展示任务名称 Tooltip；移开后消失 | ✅ |
| 5 | 08:00–18:00 任务 | 17:00 查看（含中途休眠） | 该任务 percent ≈ 90% | ✅ |
| 6 | 进度条显示中 | 打开无边框全屏游戏 | 顶栏仍可见 | ⚠️ 人工验 |
| 7 | 用户点击托盘「隐藏」 | — | 顶栏消失，`visible=false` | ✅ |
| 8 | 用户点击托盘「退出」 | — | 所有 Hope 进程结束，互拉停止 | ✅ |
| 9 | 二次启动 | — | 单实例，不重复顶栏 | ✅ |
| 10 | 无任务 | — | 不显示顶栏 | ✅ |
| 11 | 任务到期且 `expiredBehavior=notify` | 到达 endAt | 触发一次系统通知 | ✅ |
| 12 | Headless 被结束 | Desktop 仍运行 | 数秒内 Headless 被 Desktop 拉起 | ✅ |
| 13 | 任务进行中 | 托盘暂停 10 分钟后继续 | 顶栏隐藏期间进度仍推进；恢复后 percent 一致 | ✅ |

---

## 10. 风险登记

| 风险 | 影响 | 缓解 |
|------|------|------|
| 用户期望「所有全屏游戏可见」 | 差评、退款 | 文案明确 Phase 1 边界；插件单独告知 |
| WPF 顶栏悬停与穿透实现复杂 | 开发延期 | §5.4 已拆分 hit-test 与全局鼠标检测 |
| 多任务时间轴重叠段过窄 | 可读性差 | 最小段宽 2px 或合并显示策略（实现时酌定） |
| 多显示器顶栏位置错误 | 体验 | 首版仅主屏 + 设置项预留 |
| Phase 2 Hook 触发反作弊 | 封号争议 | 独立插件、强制风险提示、游戏分级 |
| 进程多开 | 双进度条 | Headless 单实例 Mutex |

---

## 11. 开放问题

1. Desktop 与 Overlay 同进程？**已决：是（`hope-desktop.exe`）** ✅
2. 开始时间？**已决：定时任务必填；即时任务 `startAt = createdAt`** ✅
3. 配置文件是否允许用户手动编辑？__________（当前可直接改 `%APPDATA%\Hope\*.json`）
4. 是否需要 `hope-headless.exe --debug` 打开控制台？**已决：是** ✅ 已实现
5. 插件是否考虑上架 Microsoft Store？__________
6. 旧版 `需求文档.md`？**建议废弃，仅作历史参考**
7. 全局「暂停」是否冻结墙钟？**已决：不冻结** ✅
8. Desktop 拉起 Headless 时是否传 `--desktop` 完成双向互拉？**待做（v0.4）**
9. 设置项是否并入配置窗体？**待做（v0.4）**

---

## 附录 A：与旧版方案差异对照

| 旧版 | 新版 |
|------|------|
| Game Bar UWP 为唯一覆盖层 | Phase 1 改为 DWM 透明窗；Game Bar 降为插件备选 |
| 首版即覆盖独占全屏 | 独占全屏移至插件 Phase 2 |
| 三进程 + UWP 沙盒管道 | 首版简化 IPC，无 AppContainer ACL |
| CI 含 MSIX 签名链 | 首版仅 Inno Setup |

## 附录 B：参考链接

- [Fullscreen Optimizations（微软 DirectX 博客）](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)
- [Game Bar 点击穿透文档](https://learn.microsoft.com/en-us/gaming/game-bar/guide/click-through)
- [In-Game Overlays 原理（Fred Emmott）](https://fredemmott.com/blog/2022/05/31/in-game-overlays.html)
