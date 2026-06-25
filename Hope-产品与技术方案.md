# Hope（盼头）产品与技术方案

> 版本：v0.4  
> 更新说明：根据仓库实际代码同步 **实现状态**；标注 Phase 1 已完成 / 部分完成 / 未做项。

**图例：** ✅ 已实现并合入代码　⚠️ 后端或局部已有，UI/联调未完成　❌ 未实现（含 Phase 2）

---

## 0. 实现状态（截至 2026-06-24，v0.4 施工中）

> 对照 `src/headless`、`src/win-desktop`、`.github/workflows/release.yml`、`setup.iss`。

### 0.1 Phase 1 总览

| 模块 | 状态 | 代码位置 / 备注 |
|------|------|-----------------|
| Headless 核心（Go） | ✅ | `src/headless/`：`engine`、`task`、`config`、`ipc` |
| 墙钟实时进度 & 多任务分段 | ✅ | `task.BuildLayout`、`task.Percent`；单测见 `task_test.go` |
| 配置持久化（JSON） | ✅ | `%APPDATA%\Hope\config.json` + `tasks.json`；含 UTF-8 BOM 剥离 |
| IPC 命名管道广播 & 命令 | ✅ | `\\.\pipe\Hope\progress`；`hide`/`show`/`getSettings`/`updateSettings` 已实现 |
| Headless 单实例 | ✅ | `Global\HopeHeadless` Mutex |
| Headless 日志 | ✅ | `logs/hope-headless.log`；`--debug` 额外输出控制台 |
| WPF 配置窗体 & 任务 CRUD | ✅ | `Views/ConfigWindow`：双 Tab、取色盘、日期时间选择器、快填、实时预览 |
| 全局设置 UI | ✅ | 刷新间隔 / 条高 / 开机自启 / 运行时显示配置窗 / 重置窗高；**修改即生效** |
| 系统托盘 | ✅ | `App.xaml.cs`：主题自适应图标、打开设置、暂停/继续、隐藏/显示、关于、退出 |
| 应用 / 托盘品牌图标 | ✅ | `src/resources/` + `AppIconHelper`；托盘随系统亮暗着色 |
| DWM 分段顶栏 Overlay | ✅ | `Overlay/OverlayWindow`：多色填充、透明未完成区、条高读回 |
| 点击穿透 & 不出 Alt+Tab | ✅ | `NativeMethods` + `WM_NCHITTEST` → `HTTRANSPARENT` |
| 悬停展示任务名 + 倒计时 | ✅ | `OverlayWindow`：`endAt` 倒计时 Tooltip |
| 跟随图片/动图 | ✅ | `Overlay/ImageSprite`：Bgra32 保留 alpha；>15px 等比缩放、GIF 播放 |
| 截止后行为 `expiredBehaviors`（多选） | ✅ | Headless + Desktop 逻辑 ✅；`keep`/`blink`/`hide` 互斥 + `notify` 叠加；全局默认 + 任务级覆盖（「使用全局默认」并入下拉）；`blink` 为柔和 alpha 渐变持续到查看 |
| 循环任务 `recurrence` | ✅ | 仅定时任务；每天 / 每 N 天 / 按星期；时分定义每日窗口、支持跨午夜；窗口结束套用到期提醒并在下次开始重置（详见 §7.2） |
| Desktop → Headless 互拉 | ✅ | `HeadlessSupervisor`：检测进程缺失则拉起 |
| Headless → Desktop 互拉 | ✅ | `main.go --desktop` 已实现；`HeadlessSupervisor` 拉起 Headless 时传入自身路径，双向互拉已接通 |
| Desktop 单实例 | ✅ | `Global\HopeDesktop` Mutex |
| WPF-UI Fluent 主题（配置窗） | ✅ | `App.xaml` 合并主题字典；`FluentWindow` + `TitleBar`；首帧后延迟应用 Mica（Win11）/ Acrylic（Win10），托盘延迟打开 + 隐藏时 `UnWatch`，规避 `Show()` 卡死 |
| CI 编译 & 单测 | ✅ | `.github/workflows/release.yml`：`go test` + `dotnet publish` |
| Inno Setup 安装包 | ✅ | `setup.iss`（含可选开机自启任务）；需打 tag `v*` 触发 Release 上传 |
| VS Code 调试配置 | ✅ | `Hope.sln` + `.vscode/launch.json`（net10.0-windows） |
| Phase 2 全屏插件 | ❌ | `src/plugins/fullscreen/` 仅占位 README |

**当前可交付边界：** v0.4 配置窗与设置链路已通，FluentWindow/Mica、双向互拉均已落地；v0.5 到期提醒已升级为「多选互斥 + 全局默认 + 任务级覆盖」，`blink` 改为柔和 alpha 渐变并持续到查看；v0.6 到期提醒「使用全局默认」并入下拉，并新增 **循环任务**（每天 / 每 N 天 / 按星期，支持跨午夜，窗口结束套用到期提醒并在下次开始重置）；距 v1.0 尚差帮助文档与验收清单人工回归。

### 0.2 验收标准对照（§9）

| # | 场景 | 状态 |
|---|------|------|
| 1 | 多任务多色拼接 | ✅ |
| 2 | 点击穿透 | ✅ |
| 3 | 不出 Alt+Tab / Win+Tab | ✅ |
| 4 | 悬停展示任务名 + 倒计时 | ✅ |
| 5 | 墙钟 90% 示例 | ✅（单测覆盖） |
| 6 | 无边框全屏可见 | ⚠️ 未自动化测；设计支持，需人工验证 |
| 7 | 托盘隐藏顶栏 | ✅ |
| 8 | 托盘退出 & 互拉停止 | ✅ |
| 9 | 单实例 | ✅ |
| 10 | 无任务不显示 | ✅ |
| 11 | 到期提醒 `expiredBehaviors`（多选） | ✅（保持/隐藏/闪烁互斥 + 通知叠加；全局默认 + 任务级覆盖） |
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
| 配置窗口 | WPF：创建/编辑任务、全局设置、实时预览 | ✅ |
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
- Desktop 退出异常时，Headless 在数秒内重新拉起 Desktop（Headless 以 `--desktop <path>` 启动）。✅ `HeadlessSupervisor` 拉起 Headless 时已传入自身路径
- 用户托盘「退出」时，双方约定正常关闭，互拉逻辑不触发。✅

---

## 4. 技术选型

### 4.1 已确定

| 领域 | 选型 | 理由 | 状态 |
|------|------|------|------|
| 后台核心 | **Go** | 单文件分发、低内存、适合无 UI 常驻 | ✅ |
| 配置与托盘 | **C# WPF** | Win32 生态成熟、开发效率高 | ✅ |
| 界面主题 | **WPF-UI（Fluent）** | 贴近 Win11、Mica/亮暗跟随；复用 WPF 运行时、增量小（§5.3.2） | ✅ FluentWindow + 延迟 Mica |
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
  "segments": [
    {
      "taskId": "uuid-a",
      "name": "任务A",
      "color": "#E53935",
      "barStart": 0.0,
      "barEnd": 30.0,
      "percent": 30.0,
      "fillEnd": 30.0
    },
    {
      "taskId": "uuid-b",
      "name": "任务B",
      "color": "#43A047",
      "barStart": 30.0,
      "barEnd": 60.0,
      "percent": 60.0,
      "fillEnd": 60.0
    },
    {
      "taskId": "uuid-c",
      "name": "任务C",
      "color": "#FDD835",
      "gif": "C:\\Users\\me\\AppData\\Roaming\\Hope\\gifs\\cat.gif",
      "barStart": 60.0,
      "barEnd": 90.0,
      "percent": 90.0,
      "fillEnd": 90.0
    }
  ]
}
```

> 上例对应 a=30%、b=60%、c=90%：色段 `[0,30]`、`[30,60]`、`[60,90]`，`[90,100]` 无色段（透明）。布局规则见 §7.2「进度条分段模型 v2」。

| 字段 | 类型 | 说明 |
|------|------|------|
| `version` | int | 协议版本 |
| `visible` | bool | 是否显示顶栏（无任务时为 `false`） |
| `state` | string | `idle` / `running` / `paused` / `expired`；`paused` 时 `visible=false`，但 `segments` 仍按墙钟更新 |
| `segments[]` | array | 按 percent 升序拼接的色段，**同一物理顶栏**；详见 §7.2 v2 |
| `segments[].color` | string | 用户为任务指定的颜色（`#RRGGBB`） |
| `segments[].gif` | string | 可选；任务的本地**图片/动图**路径，挂在进度条**下方**、跟随该段右边界（`fillEnd` = 其 `percent`）移动；动图循环播放 |
| `segments[].barStart` | float | 该色段左边界 = 前一任务 percent（首段为 0），0–100 |
| `segments[].barEnd` | float | 该色段右边界 = 本任务 percent，0–100 |
| `segments[].percent` | float | 该任务自身进度 0–100（= `barEnd`） |
| `segments[].fillEnd` | float | = `barEnd`（整段满涂）；保留字段供 Overlay 绘制与 GIF 定位 |
| `segments[].endAt` | string (RFC3339) | 该任务截止时刻；供 Overlay 悬停计算倒计时（§5.4 修改 1） |

**分段布局规则（v2，已确定，详见 §7.2）：**

1. 仅包含 **未过期** 的任务；全局 `state=paused` 时仍按墙钟计算 `percent`，但不向 Overlay 绘制（`visible=false`）。
2. 取各活跃任务 `percent` **升序** p₁≤…≤pₙ；第 i 段占 `[p_{i-1}, p_i]`（p₀=0），满涂任务 i 颜色。
3. 段间首尾相接、无空隙；`percent` 相同的零宽段直接跳过。
4. **最大 percent pₙ 之后的 `[pₙ,100]` 完全透明**：不绘制任何底色、不可点击、悬停不交互。
5. 视觉示例（a=30/b=60/c=90）：`[==a 0–30==][==b 30–60==][==c 60–90==][  透明 90–100  ]`。

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
{"action":"updateSettings","settings":{"barHeightPx":4,"expiredBehaviors":["keep","notify"]}}
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

> 广播额外字段 `expired[]`（任务刚到期时一次性下发，携带 `behaviors[]`，供 Desktop 执行 **一次性** 提醒如 `notify`）。✅ 已实现
> `keep` / `blink` / `hide` 等 **持续** 表现由广播 `segments[]` 中每段的 `expired`（bool）与 `behaviors[]` 字段驱动：到期保留段标 `expired:true`，含 `blink` 时由 Overlay 做柔和 alpha 渐变。✅ 已实现

### 5.3 WPF 配置窗体 + 系统托盘 ✅

**配置窗体：**

- [x] 任务字段：**名称**、**颜色**（必填）、**类型**（`scheduled` / `instant`）、**截止时间**；定时任务另填 **开始时间**；可选 **跟随图片/动图**（任意图片格式，文件选择）
- [x] 支持多任务列表：新建 / 编辑 / 删除
- [x] 保存后通过 IPC 同步至 Headless
- [x] 关闭窗口时 **最小化到托盘**，不退出进程
- [x] 全局设置 Tab：进度条高度、刷新间隔、任务到期后行为、开机自启、运行时显示配置窗、重置窗高；**修改即 `updateSettings`**（无保存按钮）
- [x] `expiredBehaviors` 设置项（**显示模式单选下拉**：保持显示 / 自动隐藏 / 闪烁提醒 + **系统通知** 独立勾选框；下拉天然互斥）——此为全局默认值
- [x] 任务编辑区「到期提醒」**单行**：显示模式下拉 + 通知勾选框与标题同排。下拉含 **「使用全局默认」** 项：选中时本任务 `expiredBehaviors=null`（运行期沿用全局含通知），并 **禁用** 本任务通知勾选框；选具体模式则各存其值。**新建任务默认「使用全局默认」**
- [x] 任务编辑区「循环」（仅定时任务）：模式下拉（不循环 / 每天 / 间隔若干天循环 / 按星期）+ 条件行（间隔天数输入框，范围 3~799；按星期周一~周日两行多选）。详见 §7.2「循环任务」

#### 5.3.1 配置窗体交互增强（v0.4 新增需求）

> 来源：用户反馈现有编辑面板偏简陋，提升录入体验与数据正确性。

**需求 1：颜色用系统取色盘选取，且任务颜色不可重复** ✅（2026-06-24）

- [x] 点击颜色 **预览色块** 调起 **Windows 系统取色盘**（`ColorDialog`）。
- [x] 取色盘初始色取当前任务颜色；确认后回填大写 `#RRGGBB` 并实时刷新预览。
- [x] 手填文本框保留，与预览同步。
- [x] **颜色唯一性**：保存时校验（编辑态排除自身）；取色后撞色即时提示。

**需求 2：日期、时间改用选择器录入** ✅ → 扩展（2026-06-24）

- [x] 开始时间 / 截止时间由原 `yyyy-MM-dd HH:mm` **手填文本框** 改为 **日期选择器（`DatePicker`）+ 时（00–23）+ 分（00–59）下拉**，杜绝格式错误。
- [x] 即时任务仅截止时间；定时任务额外显示开始时间选择器（沿用现有显隐逻辑）。
- [x] 保存时校验：开始须早于截止；任一时间未选齐则提示。

**需求 3：日期选择器不透明 + 日期/时间快填** 🔲 → ✅（2026-06-24）

| 项 | 说明 |
|----|------|
| DatePicker 不透明 | 保留 **系统默认** `DatePicker` 外观；仅对 `DatePickerTextBox` / 弹出 `Calendar` 设置不透明底色与 `TextFillColorPrimaryBrush` 前景 |
| 全局设置即时生效 | 去掉「保存设置」按钮；刷新间隔、条高、自启、运行时显示等 **修改即 `updateSettings`** |
| 日期快填 | 在 **开始日期**、**截止日期** 选择器下方各一行「快填」：**今天、明天、后天、一周后、一月后**（相对**当天**日历日） |
| 时间快填 | **现在、+0.5H、+1H、+2H、+8H、+12H**（相对当前墙钟；跨日同步日期） |
| 交互 | 与名称快填一致；点击后刷新预览并触发自适应窗高 |

**需求 4：任务编辑区分组与主题细节** ✅（2026-06-24）

| 项 | 说明 |
|----|------|
| 区块分割线 | 三段：**名称~类型**（含颜色/预览）｜**开始日期+时间**｜**截止+时间**；`Separator` + `ControlStrokeColorDefaultBrush`（2px，随 Fluent 主题） |
| 编辑标题 | 原「编辑/新建」改为动态 `StatusText` 置顶（新建/正在编辑/校验提示） |
| 下拉框主题 | 使用 WPF-UI `ControlsDictionary` 默认 `ComboBox` 样式（勿在窗口级覆盖不完整隐式样式） |
| DatePicker | **不在窗口级覆盖样式**，沿用 WPF-UI `ControlsDictionary` 的 Fluent 隐式样式（深色圆角输入框 + Fluent 弹出日历）。窗口级显式 `Style`（无 `BasedOn`/`Template`）会退回系统默认模板 → 白框 + 简谱日历，故移除 |

**需求 5：编辑表单单行紧凑布局（降低窗口高度，v0.5）** ✅

> 来源：用户反馈编辑窗口过高。

- [x] **所有字段** 由「标签独占一行 + 控件另起一行」改为 **「标签在左、控件在右」单行**（`DockPanel` + 定宽 `FieldLabel` 左列，竖直对齐）。
- [x] **日期与时间分两行**：`开始日期` / `开始时间`（时 : 分）与 `截止日期` / `截止时间` 各自独立成行（同行内为「标签 + 控件」），避免一行挤不下。
- [x] 名称快填（下班/干饭/放假）移至名称输入框 **同行右侧**；日期/时间快填按钮以 `WrapPanel` 紧凑排列、左侧对齐控件列。
- [x] 预览块标签「预览」移至 **左侧同排**；去除「使用全局默认」等冗余行（见 §7.2）。
- [x] `DatePicker` 外框统一为 **主题填充/描边**（`ControlFillColorDefaultBrush` + `ControlStrokeColorDefaultBrush`），去除系统默认白底白边，与其它输入框一致。
- [x] 图片框支持 **拖放图片文件**（`AllowDrop` + `Drop`），空态 placeholder 提示「（可拖放图片到此）」。
- [x] 预览的 **百分比 + 倒计时** 文案移至 **预览块下方独立一行**（对齐控件列）。

**需求 6：表单顺序、对齐与控件细化（v0.6）** ✅

> 来源：用户 2026-06-25 反馈。

- [x] `DatePicker` **移除窗口级显式样式**：之前的显式 `Style`（无 `BasedOn`/`Template`）把 WPF-UI Fluent 隐式样式覆盖回系统默认模板，导致白框 + 简谱日历；移除后恢复 Fluent 深色圆角输入框与弹出日历（见 §5.3.1 控件主题表）。
- [x] 表单各项 **标题右对齐**（`FieldLabel`/`QuickFillLabel` 显式 `TextAlignment=Right`，紧贴右侧控件列）。
- [x] **表单顺序重排**：名称 / 颜色 / 图片 / 预览 → 分割线 → 类型 / 循环（即时任务隐藏）/ 到期提醒 → 分割线 → 开始时间组（即时隐藏）→ 分割线 → 截止时间组。即时任务不显示「循环」与「开始时间组」及其分割线（避免双分割线）。
- [x] 循环「按星期」多选改 **两行**（第一行周一~周五，第二行周六、周日），选项为单字 `[]一 []二 …`，选项间距 5px。
- [x] 循环「每 N 天」改名 **「间隔若干天循环」**；间隔由下拉改为 **输入框**，仅接受 **2~800（含端点）的整数**（保存与预览均校验）。

#### 5.3.3 配置窗体重构 + 全局设置 + 实时预览（v0.4）⚠️ 主体 ✅

> 来源：用户 2026-06-24 反馈。包含一个 bug 修复、两处修改与六项新增。

**Bug：顶栏挂载图片后出现背景色** ✅（2026-06-24）

- 已修复：`ImageSprite` 改用 `LockBits` → `Bgra32`，保留 alpha；Overlay 背景恒透明。

**修改 1：悬停提示内容** ✅（2026-06-24）

- 悬停已填充段：任务名 + **倒计时**（`segments[].endAt`）；格式见 §5.3.3 新增 2。

**修改 2：窗口默认尺寸与按钮文案** ✅ → 修订（2026-06-24）

- 配置窗 **宽度** 默认 1000px、`MinWidth=1000`；用户可自由拖拽调整宽高。
- **高度自适应编辑区**（见 **新增 7**）：按任务编辑 Tab 内容动态计算窗体高度，使编辑区 **不出现纵向滚动条**；用户手动拉高/矮后不被强制回弹。
- 修正按钮文案截断：「选择…」「清除」需完整显示（加宽按钮或改文案）。

**新增 7：窗体高度自适应 + 全局设置扩展** 🔲 → ✅（2026-06-24）

| 项 | 说明 |
|----|------|
| 编辑区无滚动条 | 任务编辑 Tab **去掉 `ScrollViewer`**，以 `TaskEditPanel` 实测高度驱动窗体高度 |
| 不限制用户调尺寸 | `ResizeMode=CanResize`；`MinHeight` 仅保留合理下限（≈400px），**不**将自适应高度写死为 `MinHeight` |
| 自适应时机 | 首次显示、任务类型切换（定时/即时显隐开始时间）、「重置窗口高度」按钮 |
| **运行时显示此窗口** | 全局设置勾选框 `settings.showConfigAtRuntime`，**默认关**；开启后 Desktop **下次启动**（IPC 连上并 `getSettings` 后）自动弹出配置窗 |
| **重置窗口高度** | 全局设置按钮：按当前编辑区内容重新计算并应用窗体高度 |

**实现要点：**

- `Settings` / `SettingsDto` 增加 `showConfigAtRuntime`（bool，默认 `false`）。
- `ConfigWindow.FitHeightToTaskEditor()`：`TaskEditPanel.Measure(…, ∞)` + Tab 头 / 外边距 / 标题栏常量 → `Window.Height`。
- `App` 在首次 `SettingsReceived` 且 `showConfigAtRuntime` 时调用 `ShowConfig()`（仅一次）。

**新增 1：选择图片后展示预览（≤15px 高）** ✅（并入新增 2 实时预览）

**新增 2：任务编辑实时预览（进度条 + 图片）** ✅（2026-06-24）

- 在任务编辑区渲染一条 **模拟真实顶栏** 的进度条 + 可选挂载图片（图片高度最高 15px）。
- 颜色、时间、图片 **实时取自编辑表单**；保存后才影响桌面真实顶栏。
- **进度条高度**：与全局设置 `settings.barHeightPx`（1–10px）**一致**；在「全局设置」Tab 调整条高下拉后，预览**即时**反映（无需先点保存）。
- **完成度展示（v2 单任务语义）**：
  - 按墙钟实时计算当前任务的 `percent`（规则与 Headless `task.Percent` 一致：定时任务用开始/截止，即时任务用 `createdAt`/截止）。
  - 预览条上仅绘制 `[0, percent]` 区段，**满涂**当前任务色；`[percent, 100]` **完全透明**（不画底色），与桌面 Overlay 一致。
  - 跟随图片挂在进度条**下方**，水平中心对齐 `percent` 位置（= 填充右边界 / `fillEnd`）。
- 预览区附带 **percent 数值 + 距截止倒计时** 文案（格式与顶栏悬停一致：≥1 天为「N 天 HH:mm:ss」，否则「HH:mm:ss」，已到期为「已到期」）。
- 表单字段变更时立即刷新；另以 **1 秒** 周期 tick 更新进度与倒计时（与真实顶栏同步感）。

**修订说明（相对初版「静态满涂示意」）：** 初版写「单段满涂 + 图片挂右侧」仅为占位；现改为按真实 `percent` 填充，避免条高固定 8px、与全局设置脱节。

**新增 3：全局设置项** ✅（2026-06-24）

- **刷新间隔**：1–10 秒（`settings.refreshSec`）；**修改即生效**。
- **进度条高度**：1–10px（`settings.barHeightPx`）；Overlay + 编辑预览即时应用。
- **开机自启**：HKCU Run + `settings.autostart`。
- **运行时显示此窗口**：`settings.showConfigAtRuntime`，默认关；下次启动自动打开配置窗。
- **重置窗口高度**：按任务编辑区内容重新 `FitHeightToTaskEditor()`。
- [x] `expiredBehaviors` 配置（显示模式单选下拉：保持显示 / 自动隐藏 / 闪烁提醒 + 系统通知独立勾选框），全局为默认值；任务编辑区「到期提醒」单行，新建任务自动同步全局默认并各存其值。

**新增 4：双 Tab 布局** ✅

**新增 5：「添加任务」按钮** ✅

**新增 6：任务名称预设快填** ✅（下班 / 干饭 / 放假）

**设置读取链路（实现要点）：**

- IPC：`getSettings` / `updateSettings`（与 `listTasks` 同机制）。
- Desktop 启动与 IPC 连上后拉取设置，应用 `barHeightPx` 到 Overlay。
- 全局设置控件变更时 **立即** `updateSettings` + `getSettings`（无「保存设置」按钮）。
- `refreshSec` 由 Headless 广播循环读取，下一帧生效。

#### 5.3.2 界面主题：贴近 Windows 11 Fluent（WPF-UI）

> 目标：让配置窗体观感贴近 Windows 11 系统界面（Fluent / Mica），同时兼顾 Win10 与「低资源常驻」定位。

**方案选型对比（已决：方案 B）**

| 方案 | 做法 | 取舍 |
|------|------|------|
| A 原生 WPF 微调 | 自写圆角/留白/强调色样式字典 | 零依赖，但仅「仿」Fluent，无 Mica、DatePicker 弹窗等深层控件不会 Win11 化 |
| **B WPF-UI 库（采用）** | 引入开源 [`WPF-UI`](https://github.com/lepoco/wpfui)（NuGet `WPF-UI` 4.2.0，MIT） | 原生 Fluent 控件 + Mica/Acrylic + 亮暗跟随系统；复用现有 WPF 运行时，内存与安装包增量小；改造量适中 |
| C WinUI 3 / Windows App SDK | 微软官方 Fluent | 100% 原生，但需额外 App SDK 运行时（内存/包体更大），Win10 拿不到 Mica，且等于重写桌面端——与 Phase 1 架构和 `<100MB` 目标冲突，**放弃** |

**采用方案 B 的设计决策**

- [x] 依赖：`WPF-UI` 4.2.0（NuGet，MIT，`net10.0-windows`）。
- [x] `App.xaml` 合并 `ui:ThemesDictionary` + `ui:ControlsDictionary`。
- [x] 配置窗体使用 `FluentWindow` + `ui:TitleBar`；`ui:Button`/`ui:TextBox`/`ui:DataGrid` + 系统 `ComboBox`/`DatePicker`。
- [x] **亮 / 暗跟随系统**：`ApplicationThemeManager.ApplySystemTheme()` + `SystemThemeWatcher.Watch`（按平台选 Mica/Acrylic）。
- [x] 主操作按钮 `Appearance="Primary"`。
- [x] **Mica / 自定义 TitleBar**：已落地。规避 Win10/Win11 `Show()` 卡死的三层措施：① 托盘菜单延迟打开（Timer + `ApplicationIdle`）；② XAML 初始 `WindowBackdropType="None"`，首帧渲染后经 `EnsureFluentBackdrop()` 延迟应用 Mica（Win11）/ Acrylic（Win10）；③ 隐藏到托盘时 `SystemThemeWatcher.UnWatch`，再次打开时重新挂载。见 `ConfigWindow.xaml.cs` / `App.xaml.cs`。
- [x] **作用范围**：仅配置窗体；Overlay 与托盘不变。

**进程互保：**

- [x] Desktop 监视 Headless（见 §3.3）
- [x] Headless 监视 Desktop（`HeadlessSupervisor` 拉起 Headless 时传 `--desktop <自身路径>`）
- [x] 托盘「退出」：Desktop 先发 `quit`，双方正常结束，禁用互拉

**托盘菜单：**

| 菜单项 | 行为 | 状态 |
|--------|------|------|
| 打开设置 | 显示配置窗体 | ✅ |
| 暂停 / 继续 | 隐藏顶栏；**不冻结墙钟** | ✅ |
| 显示进度条 / 隐藏 | 设置 `visible`（`hide`/`show`） | ✅ |
| 关于 | 版本号、独占全屏说明、插件入口 | ✅ |
| 退出 | `quit`，结束所有进程 | ✅ |

**托盘图标：** ✅（2026-06-24）

| 资源 | 路径 | 用途 |
|------|------|------|
| `hope.png` | `src/resources/hope.png` | 应用图标：嵌入 `hope-desktop.exe`（`.ico`）、配置窗体 `Window.Icon` |
| `hope-h.png` | `src/resources/hope-h.png` | 托盘小图（单色 H 形模板）；按系统主题着色 |

**托盘着色规则（已确定）：**

- 读取 Windows **应用**亮/暗设置（`AppsUseLightTheme` + WPF-UI `ApplicationThemeManager`）。
- **深色主题** → 托盘图标 **白色**（`#FFFFFF`）。
- **浅色主题** → 托盘图标 **黑色**（`#000000`）。
- 系统主题切换时 **自动刷新** 托盘图标；Overlay 顶栏不受影响。

**后续可选（未做）：**

- [ ] 常态 / 暂停 / 即将到期 是否区分图标：__________
- [ ] 气球通知规则：__________（`expiredBehavior=notify` 时已有一次性气球 ✅）

### 5.4 DWM 覆盖层（分段顶栏）✅

**窗口特性：**

- [x] 无边框、无标题栏；宽度 = 主屏宽度；进度条高度 `1~10px`（`settings.barHeightPx`，Desktop 读回并应用）
- [x] 置顶：`HWND_TOPMOST` + WPF `Topmost=True`
- [x] **不参与任务切换：** `WS_EX_TOOLWINDOW`，不出现在 Alt+Tab / Win+Tab 列表
- [x] **不可聚焦：** `WS_EX_NOACTIVATE`；`ShowActivated=false`；不抢夺前台焦点
- [x] **点击穿透：** `WS_EX_LAYERED` + `WM_NCHITTEST` → `HTTRANSPARENT`（点击与拖拽均落到下层窗口）
- [x] 背景透明；仅绘制 §5.2 所述多色拼接色带（**仅已填充部分**）
- [x] 多显示器：首版 **仅主屏**（与 §7.4 一致）

**唯一交互：悬停展示任务名** ✅

- [x] 点击仍穿透；光标在顶栏 **已填充段** 内时展示 Tooltip（任务名 + **倒计时**，§5.3.3 修改 1；原为 percent）
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
- [x] 过期 `behaviors` 含 `blink`：该到期保留段做**柔和 alpha 渐变脉冲**（`DoubleAnimation`，正弦缓动淡出/淡入），持续到用户打开设置（`AcknowledgeBlink`）后停止
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
│   ├── resources/                  # ✅ 品牌资源
│   │   ├── hope.png                # 应用图标（大图）
│   │   ├── hope.ico                # 由 hope.png 生成，嵌入 exe
│   │   └── hope-h.png              # 托盘小图（按主题着黑/白）
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

**多任务 + 单色段拼接（旧模型，将被 v2 取代）：**

- 支持多个任务同时进行；**物理上仅一根顶栏**
- 每个任务必须指定 **独立颜色**（`#RRGGBB`）
- 各任务按时间跨度映射到顶栏上的 **[barStart, barEnd]** 区间，段内按该任务 `percent` 填充；多色从左到右拼接
- 布局与 IPC 字段见 §5.2

> ⚠️ 旧模型把每个任务按 **时长占比** 分配一个固定槽位、在槽位内填充，导致槽位内出现「已填充 + 未填充」的空隙，多任务时观感为「色块—空隙—色块—空隙」。用户反馈此逻辑不符合预期，见下方 v2。

#### 进度条分段模型 v2（✅ 已确认 2026-06-24，取代上文与 §5.2 旧布局）

> 来源：用户 2026-06-24 反馈。两条核心诉求：**(1) 未填充部分必须完全透明，不得出现任何背景色；(2) 进度条按各任务进度值（percent）依次分段着色。**

**需求 1 — 未完成部分透明、无背景色**

- 顶栏只绘制「已着色的进度部分」，其余一律 **完全透明**（不画任何底色 / 背景色 / 占位色）。
- 透明区域不可点击、悬停无交互。

**需求 2 — 按 percent 排序的连续分段（嵌套式进度）**

- 取当前所有活跃任务的 `percent`，**按 percent 升序排序**，记为 p₁ ≤ p₂ ≤ … ≤ pₙ（对应任务 t₁…tₙ）。
- 顶栏从左到右划分为连续色段，**第 i 段占据 [p_{i-1}, p_i]**（p₀ = 0），填充 tᵢ 的颜色。
- **最大进度 pₙ 之后的 [pₙ, 100] 区间为透明**。
- 各色段 **首尾相接、无空隙**；位置坐标直接采用 percent 值（0–100 映射到顶栏全宽）。

**示例（三任务 a=30%、b=60%、c=90%）：**

```
位置:  0% ──── 30% ──── 60% ──── 90% ──── 100%
颜色: [  a 色  ][  b 色  ][  c 色  ][  透明  ]
悬停:    任务a     任务b     任务c     无交互
```

- `0%–30%` → a 的颜色，悬停显示任务 a
- `30%–60%` → b 的颜色，悬停显示任务 b
- `60%–90%` → c 的颜色，悬停显示任务 c
- `90%–100%` → 透明，悬停无交互

**悬停规则（推导）：** 光标位于横向位置 x（0–100）时，命中「percent ≥ x 的任务中 percent 最小者」；若没有任务 percent ≥ x（即 x > pₙ），则无交互。

**已确认决策（2026-06-24）：**

1. ✅ v2 **取代** 原「时长占比槽位」模型；§5.2 的 `barStart/barEnd/fillEnd` 语义随之改为「进度刻度」。
2. ✅ 排序键 = **percent 升序**；两任务 percent 相同导致 **零宽色段时直接不绘制**（次序无关紧要）。
3. ✅ 顶栏总宽代表 0–100% 进度刻度，**不再代表时间轴**；广播 **移除** `timelineStart/timelineEnd` 字段。
4. ✅ 跟随图片（GIF）挂在 **该任务色段右边界（= 其 percent 位置）**。
5. 影响范围：`task.BuildLayout`（Go）、IPC `segments`/`State` 字段、Overlay `Render` 与悬停命中（C#）、相关单测重写。

**IPC `segments[]` 字段在 v2 下的含义：**

| 字段 | v2 含义 |
|------|---------|
| `barStart` | 该色段左边界 = 前一任务的 percent（首段为 0） |
| `barEnd` | 该色段右边界 = 本任务 percent |
| `fillEnd` | = `barEnd`（整段满涂；保留字段以兼容 Overlay 绘制与 GIF 定位） |
| `percent` | 本任务自身 percent（= `barEnd`） |
| `color` / `name` / `gif` / `taskId` | 同前 |

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

**截止后行为（已确定）：** ✅ 逻辑与配置 UI 均已实现（v0.5 改造为多选 + 任务级覆盖）

- 提醒由 **「显示模式」单选 + 「系统通知」独立勾选** 组成，存为字符串集合 `expiredBehaviors`（取代旧的单值 `expiredBehavior`）：
  - 显示模式（三选一，天然互斥）：
    - `keep` 保持显示 — ✅ 到期后该任务以 **100% 满色段** 常驻顶栏，直到手动删除或改为隐藏
    - `blink` 闪烁提醒 — ✅ 到期后该段做 **柔和 alpha 渐变**（淡出至近透明再淡入，正弦缓动），**持续到用户打开设置查看后停止**
    - `hide` 自动隐藏 — ✅ 到期后从顶栏移出该段
  - `notify` 系统通知 — ✅ 可叠加在任意显示模式之上；到期时弹一次托盘气球
- **UI 形态**：显示模式用 **单选下拉框**（天然互斥，无需手动互斥逻辑）；`notify` 为独立 **勾选框**。集合 = `[显示模式]`（+ 勾选时追加 `notify`）。
- **全局默认 + 任务级**：
  - 全局设置中的 `expiredBehaviors` 为 **默认值**。
  - 任务到期提醒下拉含 **「使用全局默认」** 选项（任务编辑区）：选中时 `task.expiredBehaviors = null`，运行期完全沿用全局（含 `notify`），且该任务的「系统通知」勾选框 **禁用**；选具体显示模式时则存本任务自己的集合。
  - **新建任务默认选「使用全局默认」**（动态跟随全局）。
- 默认值：全局 `expiredBehaviors = ["keep"]`。旧配置中的单值 `expiredBehavior` 在加载时 **自动迁移** 为单元素数组。
- 显示判定：到期任务仅当生效行为为纯 `hide`（且不含 `keep`/`blink`）时移出顶栏；其余一律保留为满色段。

**循环任务（已确定，v0.6）：** ✅ 仅 **定时任务** 可设循环；存于 `task.recurrence`（`nil` = 单次）

- 循环模式（`recurrence.mode`，四选一）：
  - `""` 不循环 — 单次任务（默认）。
  - `daily` 每天 — 每个自然日均为发生日。
  - `everyN` 间隔若干天循环 — `interval` 为 **2~800（含端点）的整数**（UI 输入框校验），自 **开始日期（锚点）** 起每隔 N 天一次。
  - `weekly` 按星期 — `weekdays[]` 多选（`0`=周日 … `6`=周六），命中即为发生日；UI 分两行展示（周一~周五 / 周六、周日）。
- **时间窗语义**：循环任务里，`startAt` / `endAt` 的 **时分** 定义每个发生日的窗口 `[当日开始时分, 截止时分]`；`startAt` 的 **日期** 仅作锚点 / 生效起始日（早于锚点不发生）。
- **跨午夜**：当截止时分 ≤ 开始时分时，窗口顺延至 **次日** 截止（如 `22:00–06:00`）。
- **进度与到期**：`percent` 在「与当前时刻相关的最近一次发生窗口」内按墙钟实时计算；窗口结束即视为到期，**套用该任务的到期提醒**（`keep`/`blink`/`hide`/`notify`）；到 **下一次发生窗口开始** 时自动重置为进行中（引擎在任务回到活跃态时清除一次性 `notify` 标记，使每次发生都能重新提醒）。
- 计算入口：Go `task.windowAt(now)` 统一返回相关窗口；`Percent` / `IsExpired` / `EffectiveEnd` / `BuildLayout` 均基于它（非循环任务恒返回固定 `[start, end]`）。配置窗预览以同构 C# 逻辑镜像。

**无任务时（已确定）：** 不创建 / 不显示顶栏窗口（`visible = false`）

### 7.3 提醒与通知

- [ ] 提前提醒节点：__________（例：30 分钟、10 分钟、5 分钟）
- [ ] 提醒方式：顶栏变色 / 托盘 / Windows Toast / 声音
- [ ] 免打扰时段：__________

### 7.4 设置项（首版范围建议）

| 设置 | 默认 | 是否首版 | 实现状态 |
|------|------|----------|----------|
| 进度条高度 (1–10px) | 4px | 是 | ✅ 全局设置即时生效 |
| 每任务颜色 | 用户必填 | 是 | ✅ 取色盘 + 去重校验 |
| 跟随图片/动图 | 可选 | 是 | ✅ |
| 显示显示器 | 主屏 | 是 | ✅ 仅主屏 |
| 截止后行为 `expiredBehaviors`（多选） | `["keep"]` | 是 | ✅ 多选互斥 + 任务级覆盖；全局为默认 |
| 刷新间隔 `refreshSec` | 1s | 是 | ✅ 1–10s，即时生效 |
| 开机自启 | 关 | 是 | ✅ HKCU Run + 持久化 |
| 运行时显示配置窗 | 关 | 是 | ✅ `showConfigAtRuntime` |
| 语言 | 简体中文 | [ ] | ❌ 字段预留，未做 i18n |

### 7.5 首次使用引导（Onboarding）

- [ ] 是否需要：是 / 否
- [ ] 步骤草案：安装 → 设第一个 Deadline → 展示顶栏预览 → 说明独占全屏限制与插件入口

### 7.6 异常与降级（已确定）

| 场景 | 期望行为 | 状态 |
|------|----------|------|
| Headless 崩溃 | Desktop 重新拉起 Headless | ✅ |
| Desktop 崩溃 | Headless 重新拉起 Desktop | ✅ 拉起 Headless 时已传 `--desktop` |
| Overlay 被挡 / 不可见 | **Phase 1 不检测、不提示** | ✅（未做检测） |
| 独占全屏且未装插件 | **Phase 1 不检测、不提示** | ✅（未做检测） |
| 系统休眠 / 唤醒 | 唤醒后按 `time.Now()` 立即重算 | ✅ |
| 用户修改系统时间 | 按新系统时间立即重算 | ✅ |

### 7.7 非功能需求

- [x] 性能目标：CPU < 10%，内存 < 100MB（未正式压测，架构上满足）
- [x] 可靠性：Headless ↔ Desktop 互相拉起（§3.3、§7.6）—— ✅ 双向互拉已接通
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
| v0.3 | DWM 分段顶栏 + 多任务闭环 + 跟随图片 | ✅ |
| v0.4 | 设置 UI、配置窗交互增强、WPF-UI 主题（FluentWindow/Mica）、品牌图标、实时预览、窗高自适应、`expiredBehavior` UI、双向互拉 | ✅ **已完成** |
| v0.5 | 到期提醒升级：多选互斥（保持/隐藏/闪烁 + 通知）、全局默认 + 任务级覆盖、`blink` 改柔和 alpha 渐变并持续到查看、`keep` 保留满色段 | ✅ **已完成** |
| v0.6 | 到期提醒「使用全局默认」并入下拉（选中禁用本任务通知）；**循环任务**（每天 / 每 N 天 / 按星期，支持跨午夜，窗口结束套用到期提醒并在下次开始重置） | ✅ **已完成** |
| v1.0 | 安装包验收、帮助文档、Onboarding | 🔲 |
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
| 11 | 任务到期且 `behaviors` 含 `notify` | 到达 endAt | 触发一次系统通知 | ✅ |
| 11b | 任务到期且 `behaviors` 含 `keep` | 到达 endAt | 该段保留为 100% 满色常驻顶栏 | ✅ |
| 11c | 任务到期且 `behaviors` 含 `blink` | 到达 endAt | 该段柔和 alpha 渐变脉冲，打开设置后停止 | ✅ |
| 11d | 任务到期且 `behaviors` 为纯 `hide` | 到达 endAt | 该段从顶栏移出 | ✅ |
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
8. Desktop 拉起 Headless 时是否传 `--desktop` 完成双向互拉？**已完成（v0.4）**
9. 设置项是否并入配置窗体？**已完成（v0.4）**

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
