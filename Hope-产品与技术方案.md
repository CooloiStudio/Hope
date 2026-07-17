# Hope（盼头）产品与技术方案

> 版本：v0.15.108（Desktop） / Headless `0.10.28`（独立递增）  
> 更新日期：2026-07-13  
> 更新说明：对齐代码——同步 IPC（移除 pause/hide/show）、验收项、MSIX 说明与文档索引；消除与 `docs/plugin-ipc.md` 的冲突

**文档分工：** 实现状态与架构以本文为准；**IPC 字段与命令以 [`docs/plugin-ipc.md`](docs/plugin-ipc.md) + 代码为准**；历史规格见 `docs/` 归档文。索引：[`docs/README.md`](docs/README.md)。

**图例：** ✅ 已实现并合入代码　⚠️ 后端或局部已有，UI/联调未完成　❌ 未实现（含 Phase 2）

---

## 0. 实现状态（截至 2026-07-14，v0.15.104）

> 对照 `src/headless`、`src/win-desktop`、`.github/workflows/`、`setup.iss`、`scripts/test.ps1`。

### 0.1 Phase 1 总览

| 模块 | 状态 | 代码位置 / 备注 |
|------|------|-----------------|
| Headless 核心（Go） | ✅ | `src/headless/`：`engine`、`task`、`config`、`ipc` |
| 墙钟实时进度 & 多任务分段 | ✅ | Unix `startTs`/`endTs`；`task.BuildLayout` / `Percent`；单测见 `task_*_test.go` |
| 配置持久化（JSON） | ✅ | `%APPDATA%\Hope\config.json` + `tasks.json`；含 UTF-8 BOM 剥离 |
| IPC 命名管道广播 & 命令 | ✅ | `\\.\pipe\Hope\progress`；读写命令、`requestId`、写后单播快照；`screenSize` / `requestState`；详见 `docs/plugin-ipc.md` |
| Headless 单实例 | ✅ | `Global\HopeHeadless` Mutex |
| Headless 日志 | ✅ | `logs/hope-headless.log`；`--debug` 额外输出控制台 |
| WPF 配置窗体 & 任务 CRUD | ✅ | `Views/ConfigWindow`：任务列表筛选、取色盘、日期时间（可编辑下拉）、快填、自动保存；操作反馈用 **自建 Toast**；确认框用 WPF-UI `MessageBox`（关闭=取消） |
| 全局设置 UI | ✅ | 条高 / 图片最大高度 / 起始位置 / 到期提醒；高级选项；**「刷新进度条(ctrl+r)」**（Primary，位于进度条设置区首行）；开机自启 / 运行时显示配置窗 / 自动更新 / 遥测；**修改即生效**；表单 ToolTip 挂在控件/文案上（非整行） |
| 全局设置持久化 | ✅ | 启动屏幕尺寸走 `screenSize`，避免默认值经 `updateSettings` 抹掉用户设置 |
| 系统托盘 | ✅ | 打开设置(`S`) / 检查更新(`U`) / 刷新进度条(`R`) / 退出(`Q`)；菜单展开后按字母触发 |
| 应用 / 托盘品牌图标 | ✅ | 水墨「盼」/ Hope；托盘用 `hope-mini.png` 原图（**不**随主题染色，见 `AppIconHelper`） |
| DWM 分段 Overlay | ✅ | 多边位置、方向、四边环绕、任务栏避让与 Z-order |
| Overlay 透明恢复 | ✅ | 销毁重建；手动「刷新进度条」；`TaskbarCreated` / `WM_DWMCOMPOSITIONCHANGED` 自动重置 |
| 点击穿透 & 不出 Alt+Tab | ✅ | `WM_NCHITTEST` → `HTTRANSPARENT` |
| 悬停展示任务名 + 倒计时 | ✅ | Overlay Tooltip（全局光标轮询，与穿透解耦） |
| 跟随图片/动图 | ✅ | `ImageSprite`；高度由全局默认或任务覆盖（15–30px）等比缩放 |
| 截止后行为 `expiredBehaviors` | ✅ | 默认自动显示 + 可叠加 blink/celebrate/notify；全局默认 + 任务级覆盖（`keep`/`hide` 已废弃） |
| 循环任务 `recurrence` | ✅ | 仅定时任务；完成时累加 n×86400 进入下一期 |
| 任务完成 / 重建 | ✅ | 列表与编辑区「完成」；循环进下一期；已完成可「创建为新任务」 |
| Desktop ↔ Headless 互拉 | ✅ | `HeadlessSupervisor` + `--desktop`；快速退出熔断 |
| Desktop 单实例 | ✅ | `Global\HopeDesktop` Mutex |
| SessionState / WriteGuard | ✅ | 会话单源；水合与完成流防 AutoSave 竞态 |
| WPF-UI Fluent 主题 | ✅ | FluentWindow + Mica/Acrylic |
| 配置窗 Toast | ✅ | `HopeToasts`：瞬时（倒计时 0→100、可关闭）+ **Sticky 业务校验**（无倒计时/无关闭钮，条件恢复后自动关）；窗未在桌面不展示 |
| 单测与一键测试 | ✅ | `go test` + `Hope.Desktop.Tests`；`scripts/test.ps1`；CI `ci.yml` / release 发版前跑 |
| Inno Setup 安装包 | ✅ | `setup.iss`；简体中文 + 英文向导；`Hope_Setup.exe` + `.sha256` |
| MSIX 商店包 | ✅ | `pack-msix.ps1`；`.msix` / `.msixupload`（zip 仅含 msix） |
| 自动更新（全量） | ✅ | 多通道检测、SHA-256、静默升级；商店通道引导 Store；**有更新/检查结果经 Toast 提示（不弹托盘气球）** |
| 进度条位置 / 方向 / 四边 | ✅ | 全局位置 + 方向；高级「我全都要」；各边独立方向（任务级位置开启时） |
| 任务级位置覆盖 | ✅ | 高级「允许为单个任务指定展示位置」 |
| 任务级图片高度 | ✅ | 高级「允许为单个任务设置图片高度」；编辑区「使用全局图片高度」+ 滑动条；桌面解析最终高度 |
| 全屏庆祝 | ✅ | `celebrate`：非四边时复制到四边闪烁；跨边同相位呼吸 |
| 颜色 | ✅ | 取色盘；仍校验不与其他任务重复（未改为「允许重复+确认框」） |
| Phase 2 全屏插件 | ❌ | `src/plugins/fullscreen/` 仅占位 |

**当前可交付边界：** v0.15 已覆盖时间戳进度与循环、位置/图片任务级覆盖、完成流、休眠/DWM 恢复、Inno + MSIX 双通道与更新分流、稳定性门禁。距 v1.0 主要差帮助/Onboarding 与验收清单人工回归。

### 0.2 验收标准对照（§9）

| # | 场景 | 状态 |
|---|------|------|
| 1 | 多任务多色拼接 | ✅ |
| 2 | 点击穿透 | ✅ |
| 3 | 不出 Alt+Tab / Win+Tab | ✅ |
| 4 | 悬停展示任务名 + 倒计时 | ✅ |
| 5 | 墙钟进度示例 | ✅（单测覆盖） |
| 6 | 无边框全屏可见 | ⚠️ 设计支持；真全屏检测已收紧（排除桌面 Shell） |
| 7 | 无活跃段时不显示顶栏 | ✅ |
| 8 | 托盘退出 & 互拉停止 | ✅ |
| 9 | 单实例 | ✅ |
| 10 | 无任务不显示 | ✅ |
| 11 | 到期提醒 `expiredBehaviors` | ✅ |
| 12 | Headless 崩溃拉起 | ✅ |
| 13 | 休眠后墙钟进度追上 | ✅ |
| 14 | 任务级位置 / 图片高度 | ✅ |
| 15 | 进度条不被任务栏遮挡 | ✅ |
| 16–20 | 四边 / 完成流 / 颜色校验等 | ✅（详见 §9） |

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

### 1.2 产品定位

- 目标用户：需要在沉浸办公/学习时仍能感知截止时间的人（备考、自由职业、朝九晚五「盼下班」等）。
- 与番茄钟 / Focus 类差异：不强制专注时段，而是**墙钟驱动的多任务进度可视化**；顶栏非侵入、点击穿透。
- 分发：GitHub/Gitee（Inno）与 Microsoft Store（MSIX）并行；数据默认仅本机。

---

## 2. 分阶段交付策略

### Phase 1：核心包（首版必做）

覆盖 **大部分办公场景** 与 **非独占全屏游戏**（无边框全屏、窗口化、Win10+ 全屏优化下的「全屏」）。

| 模块 | 说明 | 状态 |
|------|------|------|
| Headless 核心 | Go 后台：任务计时、百分比计算、配置持久化、IPC 广播 | ✅ |
| 配置窗口 | WPF：创建/编辑任务、全局设置、实时预览 | ✅ |
| 系统托盘 | 托盘图标入口：打开设置 / 检查更新 / 刷新进度条 / 退出（访问键 S/U/R/Q） | ✅ |
| DWM 覆盖层 | 透明置顶、鼠标穿透的顶栏进度条窗口 | ✅ |
| 跟随图片/动图 | 进度条下方、随 `fillEnd` 移动 | ✅ |

**首版不做的内容：**

- 独占全屏游戏 Present Hook
- Xbox Game Bar Widget
- 游戏兼容数据库

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
| 首版安装包 | **Inno Setup + MSIX** | Inno：官网与自动更新；MSIX：微软商店（商店重签，无需本地证书链） | ✅ |
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
- [x] 接收来自 Desktop 的控制命令（任务 CRUD、退出等）
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
| `state` | string | `idle` / `running` / `expired`；无绘制段时 `visible=false` |
| `segments[]` | array | 按 percent 升序拼接的色段，**同一物理顶栏**（四边模式另含 position）；详见 §7.2 v2 |
| `segments[].color` | string | 用户为任务指定的颜色（`#RRGGBB`） |
| `segments[].gif` | string | 可选；任务的本地**图片/动图**路径，挂在进度条**下方**、跟随该段右边界（`fillEnd` = 其 `percent`）移动；动图循环播放 |
| `segments[].barStart` | float | 该色段左边界 = 前一任务 percent（首段为 0），0–100 |
| `segments[].barEnd` | float | 该色段右边界 = 本任务 percent，0–100 |
| `segments[].percent` | float | 该任务自身进度 0–100（= `barEnd`） |
| `segments[].fillEnd` | float | = `barEnd`（整段满涂）；保留字段供 Overlay 绘制与 GIF 定位 |
| `segments[].endAt` | string (RFC3339) | 该任务截止时刻；供 Overlay 悬停计算倒计时（§5.4 修改 1） |

> **命令与字段完整表**见 [`docs/plugin-ipc.md`](docs/plugin-ipc.md)。下文示例保留常用命令；已移除的 `pause`/`resume`/`hide`/`show` 不再列出。

**分段布局规则（v2，已确定）：** 完整规则、视觉示例与字段含义见 §7.2「进度条分段模型 v2」。要点：仅含已开始且需展示的任务（无段时 `visible=false`）；按 `percent` 升序拼接连续色段 `[p_{i-1}, p_i]`，段间无空隙，`[pₙ,100]` 完全透明、不可交互；**已到期且保留显示的满色段**可重叠铺在活跃段之后，供多色呼吸色板（见 Overlay）。

**单任务 percent（已确定，v0.14 时间戳模型）：**

| 类型 | 有效起止 | 公式（`nowTs` / `startTs` / `endTs` 均为 Unix 秒） |
|------|----------|------------------------------------------------------|
| **定时任务** | `startTs` + `endTs` | `nowTs < startTs` → 未开始；`startTs ≤ nowTs < endTs` → `percent = (nowTs-startTs)/(endTs-startTs)×100`；`nowTs ≥ endTs` → 已截止 |
| **即时任务** | `startTs = createdAt`；`endTs` 用户填写 | 同上 |

**客户端 → 服务端（命令，JSON）：**

```json
{"action":"createTask","task":{"id":"uuid","name":"写文档","type":"scheduled","color":"#43A047","startTs":1782189600,"endTs":1782218400}}
```

```json
{"action":"createTask","task":{"id":"uuid","name":"买菜","type":"instant","color":"#E53935","endTs":1782225600}}
```

> **兼容**：旧版 `startAt`/`endAt`（RFC3339）仅在 `startTs`/`endTs` 缺失时读取；落盘时统一迁移为时间戳并移除旧字段。UI 展示层将时间戳格式化为本地日期时间。

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
{"action":"completeTask","taskId":"uuid"}
```

```json
{"action":"deleteCompletedTasks"}
```

> **任务状态（v0.8）**：`task.status` 为枚举（`active` 进行中 / `completed` 已完成，使用字符串便于后续扩展），空值兼容旧数据并回退到旧 `completed` 布尔字段。仅 **进行中** 任务参与渲染；`completeTask` 仅在用户手动点击「完成」时将任务置为 `completed`。若被完成的任务为 **循环任务**，则先复制其全部参数生成一份新副本（全新 `id`、起止时间戳整体累加 `n×86400`、状态 `active`）再把原任务标记为 `completed`。`deleteCompletedTasks` 一键删除全部已完成任务。

```json
{"action":"updateSettings","settings":{"barHeightPx":4,"imageMaxHeightPx":15,"expiredBehaviors":["blink","notify"]}}
{"action":"screenSize","settings":{"screenWidth":1920,"screenHeight":1080}}
{"action":"requestState"}
{"action":"getVersion"}
{"action":"quit"}
```

> 广播额外字段 `expired[]`（任务刚到期时一次性下发，携带 `behaviors[]`，供 Desktop 执行 **一次性** 提醒如 `notify`）。✅  
> 持续表现由 `segments[]` 中 `expired` + `behaviors[]`（`blink` / `celebrate`）驱动。✅  
> **已移除命令：** `pause` / `resume` / `hide` / `show`（勿再实现）。完整契约见 [`docs/plugin-ipc.md`](docs/plugin-ipc.md)。

### 5.3 WPF 配置窗体 + 系统托盘 ✅

**配置窗体：**

- [x] 任务字段：**名称**、**颜色**（必填）、**类型**（`scheduled` / `instant`）、**截止时间**；定时任务另填 **开始时间**；可选 **跟随图片/动图**（任意图片格式，文件选择）
- [x] 支持多任务列表：新建 / 编辑 / 删除
- [x] 保存后通过 IPC 同步至 Headless
- [x] 关闭窗口时 **最小化到托盘**，不退出进程
- [x] 全局设置 Tab：进度条高度、图片最大高度、起始位置、到期提醒、开机自启、运行时显示配置窗、自动更新、遥测；**高级设置** 含前进方向、刷新间隔、四边、任务级位置、**任务级图片高度**；**修改即 `updateSettings`**。屏幕尺寸上报走独立 `screenSize` 命令，避免覆盖用户设置
- [x] `expiredBehaviors` 全局设置项：**呼吸提醒 / 全屏庆祝 互斥（radio，含「仅自动显示」复位项）**，系统通知为独立可叠加勾选框；默认「仅自动显示」（空集合）。选中呼吸或庆祝时在其后追加 **红色「⚠️光敏性癫痫警告⚠️」** 文案——此为全局默认值
- [x] 任务编辑区**不再暴露到期提醒选项**，所有任务默认沿用全局；任务级覆盖能力仍保留于数据模型与后端（`task.expiredBehaviors` 非空即覆盖全局），编辑既有任务时原样保留其覆盖、不被表单清空
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
| 编辑标题 | 任务编辑区顶部 `StatusText` **仅**表示新建/正在编辑；瞬时操作反馈改走 §5.3.8 Toast |
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

- [x] `DatePicker` 移除窗口级显式样式（恢复 Fluent 深色圆角日历），详见 §5.3.1 需求 4「DatePicker」控件主题表。
- [x] 表单各项 **标题右对齐**（`FieldLabel`/`QuickFillLabel` 显式 `TextAlignment=Right`，紧贴右侧控件列）。
- [x] **表单顺序重排**：名称 / 颜色 / 图片 / 预览 → 分割线 → 类型 / 循环（即时任务隐藏）/ 到期提醒 → 分割线 → 开始时间组（即时隐藏）→ 分割线 → 截止时间组。即时任务不显示「循环」与「开始时间组」及其分割线（避免双分割线）。
- [x] 循环「按星期」多选改 **两行**（第一行周一~周五，第二行周六、周日），选项为单字 `[]一 []二 …`，选项间距 5px。
- [x] 右侧新增 **「关于」Tab**（与「全局设置」「任务编辑」并列），显示三段式版本号 `vX.Y.Z`（取自程序集 `Version`，于 `Hope.Desktop.csproj` 维护，便于确认构建）。
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
- [x] `expiredBehaviors` 全局设置：**呼吸提醒 / 全屏庆祝 互斥 radio（含「仅自动显示」复位项）+ 系统通知独立可叠加勾选框**，默认「仅自动显示」（空集合）；任务编辑区不暴露到期提醒选项，任务级覆盖保留于数据模型。完整语义见 §7.2「截止后行为」。

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

| 菜单项 | 访问键 | 行为 | 状态 |
|--------|--------|------|------|
| 打开设置 | `S` | 显示配置窗体 | ✅ |
| 检查更新 | `U` | 手动检查 GitHub / 商店更新 | ✅ |
| 刷新进度条 | `R` | 同 §5.4.2 入口 A（重置进行中即时任务起点 + 销毁重建 Overlay） | ✅ |
| 退出 | `Q` | `quit`，结束所有进程 | ✅ |

> 访问键：托盘菜单展开后按对应字母即可；文案带 `(&S)` 等形式。暂停/隐藏等旧项已收敛，当前以以上四项为主。

**托盘图标：** ✅（2026-06-24）

| 资源 | 路径 | 用途 |
|------|------|------|
| `hope.png` | `src/resources/hope.png` | 应用图标：嵌入 `hope-desktop.exe`（`.ico`）、配置窗体 `Window.Icon` |
| `hope-mini.png` | `src/resources/hope-mini.png` | 托盘小图：白底品牌图，**不做主题着色**（`AppIconHelper`） |

**托盘图标规则（已确定）：**

- 托盘直接使用 `hope-mini.png` 原图，不随系统亮/暗染色（避免水墨字被冲掉）。
- Overlay 顶栏不受托盘图标影响。

**后续可选（未做）：**

- [ ] 常态 / 即将到期 是否区分图标：__________
- [ ] 气球通知：`expiredBehavior=notify` 曾用托盘气球；现行以系统通知 / Toast 为主 ✅

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
- [x] 过期 `behaviors` 含 `blink`（或 `celebrate`）：做**颜色轮换呼吸**——每条边收集本边闪烁段的**去重颜色**（按 taskId 排序）组成序列，单色补一个透明（色↔透明呼吸），多色在各色间循环平滑过渡（正弦缓动，每步 `BlinkStepSec=0.8s`）。**持续到任务不再到期**（重新设定时间 / 删除 / 完成）；打开/激活设置窗口**不**停止呼吸
  - 单任务：透明 ↔ 任务色 呼吸；两任务：A ↔ B；三任务：A→B→C→A…，以此类推（频率不变，仅轮换颜色）
  - 本边所有闪烁段**共用一支可动画画刷**，动画挂在画刷上、与矩形几何解耦：未完成任务每秒推进重建矩形不会打断呼吸；仅颜色序列变化才重建动画
  - [x] **闪烁连续性**：闪烁矩形按 `taskId`（+所在边）**持久化复用**，几何/颜色/到期态未变时不重建、不重启动画——即使**其他未完成任务每秒推进进度触发刷新**，已完成任务的闪烁相位也不被打断、不被重置（详见 §5.4.1 实现要点）
- [x] **跟随图片/动图：** 含 `gif` 的段在进度条**下方**渲染（`ImageSprite`），水平中心对齐 `fillEnd`、随进度移动
  - [x] 支持常见图片格式（GIF / PNG / JPG / BMP / WebP / TIFF 等）；**动画 WebP / APNG 仅首帧**（`System.Drawing` 限制）
  - [x] 图片高度默认取全局 `imageMaxHeightPx`（15–30）；任务可覆盖（见高级选项）
  - [x] 存在图片时窗口向外侧扩展（条高 + 图片区）；图片区点击穿透

#### 5.4.2 Overlay 透明恢复（刷新进度条）✅

> 背景：WPF `AllowsTransparency` 分层窗在 **explorer.exe 重启** 或 **UAC 安全桌面返回** 后，DWM 合成偶发丢失 per-pixel alpha，未绘制区域由透明变成不透明白底。同窗 `Invalidate` 通常无效，需 **销毁 Overlay 实例并重新创建**。

**重置原语 `ResetOverlays`：**

1. 对当前所有 `OverlayWindow` 调用 `ForceClose` 并清空字典  
2. 按当前会话设置（及最近一次 state 所需边）`EnsureOverlays` 重新实例化  
3. `requestState` / 相关快照，使新窗立即接到分段数据  

**入口 A — 用户主动「刷新进度条」**（说明仅放 ToolTip，无大段正文提示）：

**UI 位置与样式：**

- 全局设置 → **「进度条设置」分区第一行**；按钮文案 `刷新进度条(ctrl+r)`；`Appearance=Primary`（蓝色主按钮）
- 托盘菜单「刷新进度条(&R)」

**快捷键（设置窗激活且为键盘焦点时）：**

| 快捷键 | 行为 |
|--------|------|
| `ctrl+r` | 刷新进度条（同按钮） |
| `ctrl+n` | 添加任务（按钮文案不变；ToolTip 注明快捷键） |

**执行顺序：**

1. 用户点击按钮 / 托盘项 / `ctrl+r`  
2. 将所有 **进行中的即时任务**（`type=instant`、未完成、已开始且未到期，且重置后 `createdAt < endTs`）的起点重置为点击时刻（写 `createdAt` + 对齐 `startTs`，经 `updateTask`）  
3. 再执行 `ResetOverlays`  

顺序必须为：**主动触发 → 重置即时任务起点 → 销毁重建 Overlay**。  
定时任务、已完成/已到期即时任务不改时间。

**入口 B — 系统事件（只重建，不改任务时间）：**

| 信号 | 含义 |
|------|------|
| `TaskbarCreated` | explorer / Shell 重启 |
| `WM_DWMCOMPOSITIONCHANGED` | DWM 合成变化（含自 UAC 安全桌面返回等） |

事件回调经短防抖后调用 `ResetOverlays`，**不得**重置任务时间。

#### 5.3.4 配置窗 ToolTip 规范 ✅

- ToolTip **只挂在按钮、标签文案或紧凑控件本身**（如勾选框/单选项的内容命中区），**禁止**挂在整行 `DockPanel` / 拉满宽度的空白区域上，以免悬停行内空白也弹出说明。  
- 勾选框等默认横向拉满时，应 `HorizontalAlignment=Left`，使命中区约等于文案宽度。  
- **全局设置**与**任务编辑**表单控件应有说明性 ToolTip（覆盖主要字段）；**关于**页不要求补齐 ToolTip。  
- 「刷新进度条」的副作用说明（重建 Overlay + 重置进行中即时任务起点）及 `ctrl+r` 仅出现在该按钮/托盘项的 ToolTip（按钮文案已含 `(ctrl+r)`）；「添加任务」ToolTip 注明 `ctrl+n`，按钮文案不改。

#### 5.3.5 配置窗确认对话框（WPF-UI）✅

- 封装：`HopeDialogs.ConfirmAsync`（WPF-UI Fluent `MessageBox`）。  
- 覆盖场景：删除任务、批量删除已完成、完成任务、重建为新任务、安装更新、彩蛋。  
- **关闭按钮 / Esc / 「取消」** 与取消同义，返回非 Primary，**不执行**危险操作。  
- **主按钮**为确认（文案随场景：删除 / 完成 / 确定…）；危险操作主按钮用 Danger 外观。  
- 致命错误（进程无法继续，如 `App` 启动失败）仍可用系统 MessageBox；**取色盘**暂维持系统 `ColorDialog`；许可证只读窗、托盘菜单不在此列。

#### 5.3.6 任务列表选中样式 ✅

- 选中行：行背景高亮（`SystemAccentColorLight2`）；**单元格文字颜色不变**（避免起止时间难辨）。
- **仅保留主题/DataGrid 原有行首选中指示**；不额外叠加独立蓝色行头色块（曾用 `DataGridRowHeader` 6px 色条，已撤销）。
- `HeadersVisibility=Column`（不显示行头列）。

#### 5.3.7 配置窗快捷键 ✅

- 仅当**设置窗口激活且具有键盘焦点**时生效（`InputBindings` / `CommandBindings`）。  
- `ctrl+r` → 刷新进度条（`NavigationCommands.Refresh`）  
- `ctrl+n` → 添加任务并切到任务编辑（`ApplicationCommands.New`）

#### 5.3.8 配置窗 Toast（操作反馈）✅

> 统一名称：**Toast**（自建轻量浮层，`HopeToasts`）。**不再使用** WPF-UI `Snackbar` / `SnackbarPresenter`（默认体积过大；改模板易与 `IsShown` 动画冲突导致消失/卡死）。

**职责拆分：**

| 通道 | 用途 |
|------|------|
| 任务编辑区 `StatusText` | **仅**编辑态标题：`新建任务` / `正在编辑：…` |
| Toast（瞬时） | 操作结果反馈：保存/删除/完成/刷新进度条/更新检查结果等（有倒计时、可关闭） |
| Toast（Sticky） | **业务校验未通过**时常显：如「截止时间不能早于开始时间」；无倒计时、无关闭钮；逻辑恢复后自动关 |
| 关于页 `UpdateStatusText` + 进度条 | 更新流程的**持续状态与控件**（下载进度、按钮显隐）；**用户可读提示同时发 Toast** |
| WPF-UI `MessageBox` | 需确认的危险操作（删除等），不进 Toast |
| 托盘气球 | **仅**任务到期 `notify` 等非更新提醒；**更新相关不再弹气球** |

**展示条件：**

- 仅当配置窗**已在桌面显示**时弹出（`IsVisible`，不要求聚焦）。  
- 窗体隐藏到托盘、未创建或未 Show 时：**不展示** Toast（含托盘触发的「刷新进度条」、后台自动检查更新）。

**布局与队列：**

- 锚定配置窗**底部水平居中**，**自下而上**叠放；瞬时 Toast 最多 **3** 条同时可见（Sticky **不计入**该上限，也不会被瞬时队列挤掉）。  
- 单条：最大宽度约 360px；字号 11；单行省略；条间距 4px。  
  - 瞬时：内容高度约 **22px**（含底部 2px 倒计时条）；右侧 **×**；默认约 4s 超时。  
  - Sticky：内容高度约 **20px**（**无**倒计时条、**无**关闭钮）。  
- 瞬时：相同文案已在展示中时不新增，只刷新停留时间与倒计时条。  
- 瞬时倒计时条：从左到右 **0→100**（`ScaleX 0→1`）。

**Sticky（业务校验）封装：**

- API：`HopeToasts.ShowSticky(key, message, level)` / `ClearSticky(key)`。  
- 同 `key` 只保留一条；再次 `ShowSticky` 仅更新文案与等级。  
- **不**自动超时；用户**不能**点 × 关闭；业务条件恢复后由调用方 `ClearSticky`（或 `ToastFormValidation(null)`）关闭。  
- 配置窗封装：`ToastFormValidation` / `ClearFormValidationToast`（key=`task-form-validation`）。  
- 自动保存：`BuildCurrentDto` 失败且带 `_buildDtoError`（含截止早于开始、非法时分等）→ Sticky；DTO 构建成功 → 立即清除；切换任务 / 新建任务时清除。  
- 瞬时仍用于：已创建/已更新、删除/完成结果、未连接、取色冲突提示等一次性反馈。

**主题（对齐 WPF-UI Fluent）：**

- 表面：**不透明**实底（优先 `SolidBackgroundFillColorBaseBrush`）；**无描边**  
- 圆角 4；正文 `TextFillColorPrimaryBrush`  
- 左侧色条按等级：Success / Info / Caution / Danger  
- 瞬时倒计时条用等级色 / 强调色

**封装：** `HopeToasts`（挂在 `ConfigWindow` 的 `ToastHost`）。

**自动保存：** 「已创建」「已更新」均弹瞬时 Toast；同文案只刷新计时。业务校验失败走 Sticky；未连接等仍弹瞬时 Danger。
#### 5.4.1 正向/反向渲染逻辑

> 进度条支持 **正向（forward）** 与 **反向（reverse）** 两种填充方向，可全局设置或按任务覆盖。

**方向判断优先级：**

1. 段级方向：`Segment.Direction`（如果非空且有效）
2. 窗口级方向：`OverlayWindow.Direction`（全局设置）
3. 判断方法：`IsSegReverse(seg)` → 段级优先，回退到窗口级

**正向渲染（forward）：**

- 填充区 = `[BarStart, FillEnd]`
- 矩形从左/上开始，向右/下增长
- 示例（顶部进度条）：矩形左边缘 = `BarStart% × 屏幕宽度`，宽度 = `(FillEnd - BarStart)% × 屏幕宽度`

**反向渲染（reverse）：**

- 填充区 = `[BarEnd - (FillEnd - BarStart), BarEnd]`
- 矩形从右/下开始，向左/上增长
- 关键计算：
  ```
  localStart = BarEnd - (FillEnd - BarStart)  // 填充区左/上边界
  localFill  = BarEnd                        // 填充区右/下边界（固定）
  ```
- 示例（顶部进度条）：
  - 矩形右边缘 = `BarEnd% × 屏幕宽度`
  - 矩形宽度 = `(FillEnd - BarStart)% × 屏幕宽度`
  - 矩形左边缘 = `右边缘 - 宽度`

**图片位置计算（UpdateSprites）：**

- 正向：填充前沿 = `FillEnd`
- 反向：填充前沿 = `BarEnd - (FillEnd - BarStart)`
- 图片中心对齐填充前沿，随进度移动

**悬停检测（OnHoverTick）：**

- 正向：鼠标位置百分比 = `(光标位置 / 屏幕尺寸) × 100`
- 反向：鼠标位置百分比 = `100 - (光标位置 / 屏幕尺寸) × 100`
- 仅已填充（彩色）部分响应悬停，透明区域不交互

**实现要点：**

- 渲染签名（`BuildRenderSignature`）包含方向信息，方向变化时触发重绘
- 反向渲染的矩形坐标计算在 `Render()` 方法中完成，避免修改 Headless 计算逻辑
- 前后端分离：Headless 计算百分比，前端根据方向换算成像素坐标
- **后端 `BuildLayout` 多到期任务不再被合并（颜色轮换的前提）**：旧逻辑把任务按进度升序嵌套成相邻色带，并跳过零宽段（`p <= prev`）。多个已到期任务的进度都是 100%，于是只有第一个生成 `[0,100]` 段、其余被当零宽丢弃 → 前端只收到一种颜色。修复：拆分活跃 / 到期两类——活跃仍按升序嵌套成相邻色带；**到期任务各自铺满「剩余带」`[maxActivePct,100]`（互相重叠）**，按 `taskId` 稳定排序。单个到期任务时几何与原行为一致；多个到期任务则保留全部颜色，供前端去重取色做轮换呼吸。
- **闪烁＝颜色轮换呼吸（统一模型，覆盖四种模式）**：
  - 动机：旧「每段一个透明度动画 + 相位错峰」模型，多个半透明色叠加只会混色，无法表达"一次只亮一个颜色"，多任务始终看不出交替。改为**颜色轮换**。
  - 序列：每个 `OverlayWindow` 收集**本边**闪烁段（到期 + 含 `blink`/`celebrate` + 未确认）的**去重颜色**（按 `taskId` 排序）组成 `seq`；单色时 `seq = [色, 透明]`，多色直接为去重色序列。
  - 动画：用 `ColorAnimationUsingKeyFrames`（正弦缓动）让 `seq` 各色每步 `BlinkStepSec=0.8s` 循环平滑过渡，`RepeatBehavior.Forever`。**本边所有闪烁段共用一支 `SolidColorBrush`**，动画挂在画刷的 `Color` 上。
  - 几何解耦：动画在画刷上，与矩形几何无关——未完成任务每秒推进重建矩形不会打断呼吸；仅 `seq`（颜色序列签名）变化才重建动画。
  - 同步：用全局锚点 `BlinkAnchorUtc` + `Clock.Seek((now-anchor)%总时长)` 对齐相位。**各边 `seq` 相同时（四边环绕 / 全屏庆祝）即同步显示同一颜色**；非四边环绕 + 非庆祝、任务各在不同边时**各边按各边的 `seq` 各算**。
- [x] **打开设置不停止呼吸**：打开/激活设置窗口不再触发 `AcknowledgeBlink`，呼吸持续到任务本身不再到期。（`AcknowledgeBlink` 仍保留为可选 API，当前无调用点。）

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
├── setup.iss                       # ✅ Inno Setup（Hope_Setup.exe）
├── packaging/                      # ✅ MSIX 清单模板与说明
├── scripts/pack-msix.ps1           # ✅ MSIX 打包（商店）
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
- **尺寸规则：** 默认按全局 `imageMaxHeightPx`（15–30）等比缩放；开启 `advancedImageHeight` 且任务 `imageMaxSize>0` 时按任务值缩放。最终像素由 Desktop 解析。
- **进度条本身粗细不受图片影响**，恒为 `barHeightPx`；图片在进度条旁独立成区
- 图片不影响计时与穿透；文件缺失或损坏时静默跳过
- IPC / 任务字段名仍为 `gif`（历史命名），语义为任意图片路径

**任务类型与 percent（已确定）：** 字段与计算公式见 §5.2「单任务 percent（已确定，v0.14 时间戳模型）」；时间戳模型见下文。

**时间语义（已确定，v0.14 时间戳模型）：**

- 业务逻辑 **一律使用 Unix 秒时间戳**（`startTs` / `endTs` / `nowTs`），不做日历日拆解或「拼回今天」
- 校验：`startTs ≤ endTs` 且 `endTs > startTs`（正时长）；跨午夜任务在创建时直接将截止存为次日时刻（如 22:00→次日 06:00）
- 进度 **一律墙钟实时计算**，不累加有效工作时间
- **休眠不改变公式**：休眠期间时间轴照常推进，唤醒后与未休眠使用同一 `nowTs`
- **显示与否不改变公式**：顶栏隐藏或进程短暂断开时，进度仍只由墙钟决定；Desktop 经 `requestState` 拉回最新帧
- 展示层（列表日期列、表单日期选择器）仅负责时间戳 ↔ 本地日期时间格式化

**截止后行为（已确定）：** ✅ 逻辑与配置 UI 均已实现（v0.8 改造为「默认自动显示 + 可叠加勾选」）

- **新模型**：到期 **默认自动显示**（保留 100% 满色段常驻顶栏，无需任何选项）；在此之上可 **叠加勾选** 附加效果，存为字符串集合 `expiredBehaviors`：
  - `blink` 呼吸提醒 — ✅ 到期后该段做 **柔和 alpha 渐变**（淡出至近透明再淡入，正弦缓动），**持续到任务不再到期**（打开设置不停止）
  - `celebrate` 全屏庆祝 — ✅ 到期时四边同步闪烁庆祝
  - `notify` 系统通知 — ✅ 到期时触发一次系统通知（不再依赖托盘气球作为主路径）
- **已移除**：旧的「保持显示 `keep`」「自动隐藏 `hide`」与显示模式下拉。`keep`/`hide` 仅保留为兼容常量，加载旧数据时被过滤丢弃（`sanitizeBehaviors`）。到期任务一律保留显示（`KeepsVisibleWhenExpired` 恒为 `true`）。
- **UI 形态**：仅 **全局设置** 暴露选项——**呼吸提醒 / 全屏庆祝 互斥**（radio，含「仅自动显示」复位项），**系统通知** 为独立可叠加勾选框；空集合 = 仅自动显示。选中呼吸或庆祝时其后追加 **红色「⚠️光敏性癫痫警告⚠️」**（二者均含闪动，提示光敏性风险）。
- **全局默认 + 任务级**：
  - 全局设置中的 `expiredBehaviors` 为 **默认值**（默认空集合）；呼吸与庆祝互斥意味着同一时刻最多其一生效（系统通知可叠加）。
  - **任务编辑表单不再暴露到期提醒选项**，所有任务默认沿用全局（`task.expiredBehaviors = null`）。
  - 任务级覆盖能力仍保留于数据模型与后端（`task.expiredBehaviors` 非空即覆盖全局）；编辑既有任务时表单原样保留其覆盖值、保存不清空，仅不提供编辑入口。
- 默认值：全局 `expiredBehaviors = []`。`updateSettings` 携带完整用户意图，**允许把该集合显式清空** 并持久化（旧的「非空才覆盖」合并仅用于加载兜底）。

**循环任务（已确定，v0.14 时间戳模型）：** ✅ 仅 **定时任务** 可设循环；存于 `task.recurrence`（`nil` = 单次）

- 循环模式（`recurrence.mode`，四选一）：
  - `""` 不循环 — 单次任务（默认）。
  - `daily` 每天 — 完成时 `startTs`/`endTs` 各 **+1×86400**。
  - `everyN` 间隔若干天 — `interval` 为 **2~800（含端点）的整数**（UI 校验）；完成时各 **+interval×86400**。
  - `weekly` 按星期 — `weekdays[]` 多选（`0`=周日 … `6`=周六），UI 分两行展示；完成时各 **+7×86400**（`weekdays` 仍用于 UI 标注，运行期不自动按星期重算窗口）。
- **时间窗语义**：每一期任务由存储的 **`startTs` / `endTs` 绝对时间戳** 定义唯一窗口；过期后 **保持已截止**，不自动进入「第二天」新窗。
- **跨午夜**：创建任务时截止直接存为次日时刻（`endTs > startTs`），运行期无需特殊分支。
- **进度与到期**：`nowTs` 与 `startTs`/`endTs` 比较；窗口结束即到期并套用到期提醒；**仅用户点「完成」** 才生成下一期（起止戳累加）。
- 计算入口：Go `task.effectiveStartTs()` / `effectiveEndTs()` + Unix 比较；`Percent` / `IsExpired` / `BuildLayout` 均基于此。Desktop 列表/托盘以同构 C# `TaskSchedule` 镜像（只读展示）。

**无任务时（已确定）：** 不创建 / 不显示顶栏窗口（`visible = false`）

### 7.3 提醒与通知

- [ ] 提前提醒节点：__________（例：30 分钟、10 分钟、5 分钟）
- [ ] 提醒方式：顶栏变色 / 托盘 / Windows Toast / 声音
- [ ] 免打扰时段：__________

### 7.4 设置项（首版范围建议）

| 设置 | 默认 | 是否首版 | 实现状态 |
|------|------|----------|----------|
| 进度条高度 (1–10px) | 4px | 是 | ✅ 全局设置即时生效 |
| 图片最大高度 `imageMaxHeightPx` (15–30px) | 15px | 是 | ✅ 全局默认；高级开启 `advancedImageHeight` 后任务可用 `imageMaxSize`（0=沿用全局）覆盖；**展示高度由 Desktop 解析**，Headless 仅透传 |
| 允许任务级图片高度 `advancedImageHeight` | 关 | 是 | ✅ 高级选项；编辑区「使用全局图片高度」+ 滑动条 |
| 每任务颜色 | 用户必填 | 是 | ✅ 取色盘；仍禁止与其他任务同色 |
| 跟随图片/动图 | 可选 | 是 | ✅ |
| 显示显示器 | 主屏 | 是 | ✅ 仅主屏 |
| 截止后行为 `expiredBehaviors`（可叠加） | `[]` | 是 | ✅ 默认自动显示 + 叠加 blink/celebrate/notify；全局默认 + 任务级覆盖 |
| 刷新间隔 `refreshSec`（高级设置） | 1s | 是 | ✅ 1–10s，即时生效 |
| 进度条位置 `barPosition` | top | 是 | ✅ 顶/底/左/右 |
| 进度条前进方向 `barDirection`（高级设置） | forward | 是 | ✅；任务级位置开启时可用各边 `barDirections` |
| 四边环绕 `allFour` | 关 | 是 | ✅ |
| 任务级位置 `advancedPosition` | 关 | 是 | ✅；任务 `position` 空=沿用全局 |
| 开机自启 | 关 | 是 | ✅ HKCU Run + 持久化 |
| 运行时显示配置窗 | 关 | 是 | ✅ `showConfigAtRuntime` |
| 自动下载更新 `autoUpdate` | 开 | 是 | ✅ |
| 允许遥测 `allowTelemetry` | 开 | 是 | ✅ 匿名活跃；可关 |
| 语言 | 简体中文 | [ ] | ⚠️ 安装向导中英已支持；应用内 i18n 未做 |

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
| v0.7 | 进度条位置/方向（含四边）、庆祝、任务级位置、完成按钮 | ✅ **已完成**（颜色仍保持互斥，未改「可重复+确认」） |
| v0.8–v0.13 | 时间戳进度与循环、安装/更新/商店包、图标与中文安装向导、任务栏避让、稳定性测试门禁等 | ✅ **已完成**（详见 CHANGELOG） |
| v0.14 | 任务级图片高度；列表危险操作与图标刷新等（详见 CHANGELOG v0.14） | ✅ |

| v0.15 | 刷新进度条；顶栏透明恢复；底部 Toast（含校验常显）；Fluent 确认框；快捷键 | ✅ **进行中**（v0.15.104） |
| v1.0 | 安装包验收、帮助文档、Onboarding | 🔲 |
| v1.x-plugin | 全屏游戏拓展包（独立发版） | ❌ |

---

## 8. CI/CD（Phase 1）✅

**`release.yml` 步骤（触发：在 `release` 分支可达的提交上打 tag `v*` 并推送）：**

1. [x] Checkout；校验 tag 提交在 `release` 分支上
2. [x] Setup Go、.NET 10 SDK
3. [x] 读取桌面端 `Hope.Desktop.csproj` 的 `<Version>`，并与 tag 版本一致
4. [x] 从 `CHANGELOG.md` 抽取 `### v<版本>` 小节作为 Release 正文（抽取时去掉常见 Markdown 标记，便于客户端纯文本展示）；抽不到则回退 GitHub 自动生成
5. [x] 编译 `hope-headless.exe`
6. [x] `go test ./...`
7. [x] 编译 `hope-desktop`（`dotnet publish` → `stage/`）
8. [x] Inno Setup 打 **`Hope_Setup.exe`**（官网 / 自动更新；`setup.iss` 设 `AppMutex=Global\HopeDesktop` + `CloseApplications=yes`）
9. [x] **`scripts/pack-msix.ps1`** 打 **`Hope_<版本>_x64.msix`** 与 **`.msixupload`**（微软商店；身份见 `packaging/README.md` 与 Secrets `HOPE_MSIX_IDENTITY_NAME` / `HOPE_MSIX_PUBLISHER`）
10. [x] 计算 `Hope_Setup.exe.sha256` 与 `*.msix.sha256`
11. [x] 以 `v<桌面端版本>` 创建 GitHub Release，上传 **`.exe`、`.sha256`、`.msix`、`.msixupload`**

**双通道制品：**

| 制品 | 渠道 | 说明 |
|------|------|------|
| `Hope_Setup.exe` | GitHub / Gitee Release、自动更新 | 保留现有 Inno 流程，不变 |
| `Hope_<版本>_x64.msix` | 微软商店直接上传 / Package URL | 未签名 MSIX，可由商店重签 |
| `Hope_<版本>_x64.msixupload` | Partner Center 手动上传 | zip：**仅含** `.msix`（勿内嵌独立 `AppxManifest.xml`） |

**仍不需要：**

- 本地 MSIX 自签名证书链（商店托管签名）
- `Add-AppDevPackage.ps1`

---

## 8.1 自动更新（全量）✅

桌面端内置全量更新流程：检测最新版本 → 提示 → 下载校验 → 静默就地升级 → 自动重启。

**版本检测（多通道兜底，应对大陆网络）：** `Services/UpdateService.cs` 依次尝试

1. GitHub API `repos/CooloiStudio/Hope/releases/latest`（信息最全：tag + body + 资产直链）
2. GitHub 网页 `releases/latest` 的 302 重定向解析 tag（API 被墙但网页可达时）
3. Gitee API `gitee.com/api/v5/repos/CooloiStudio/Hope/releases/latest`（大陆可达兜底）

**下载与校验：** 安装包优先从 GitHub 直链下载，失败时切换到 Gitee Release 资产直链（主通道为 GitHub 时，会经「Gitee 最新版本与主通道一致」校验后追加为兜底）。下载后比对 `Hope_Setup.exe.sha256`，不匹配则换通道重试；取不到校验文件时退化为体积下限检查。

> 前提：需在 Gitee 同名仓库 `gitee.com/CooloiStudio/Hope` 同步对应 Release，并附 `Hope_Setup.exe` 与 `Hope_Setup.exe.sha256` 资产。

**安装：** 经用户确认后，以 `Hope_Setup.exe /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS` 静默升级，并由 `cmd` 串联 `start` 在安装完成后重新拉起桌面端（`setup.iss` 设 `RestartApplications=no` 避免重复拉起）。安装永远需要用户显式点击，不会无提示重启。

**全局开关：** `config.Settings.AutoUpdate`（默认开，指针类型向后兼容旧配置）。

- 开：发现新版本后自动后台下载，就绪后提示安装。
- 关：仅检测并提示，不自动下载；用户可在「关于」页手动「下载并更新」。

**状态机（`Services/UpdateCoordinator.cs`）：** `Idle / Checking / UpToDate / Available / Downloading / Ready / Failed`，由托盘（气泡 + 「检查更新」菜单）与「关于」页（状态、进度、检查/下载/安装/跳过/发布页按钮）共享。

- 检查频率：启动后 ~25s 首检，之后每天一次；托盘菜单可手动触发。
- 「跳过此版本」记录于桌面端本地 `%APPDATA%\Hope\update.json`，自动检查时不再提示该版本（手动检查仍提示）。

**许可证：** 「关于」页「许可证」按钮弹出第三方 SDK / 库 / 组件清单与许可证（WPF-UI、.NET/WPF/WinForms、Go 标准库、`Microsoft/go-winio`、`golang.org/x/sys`、Inno Setup），并附 MIT 与 BSD-3-Clause 全文。所引用组件均为宽松型许可（MIT / BSD-3-Clause），与本项目 MIT 许可证兼容。

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
| 7 | 用户需隐藏顶栏 | — | 无活跃/展示段时不显示；或退出应用 | ✅ |
| 8 | 用户点击托盘「退出」 | — | 所有 Hope 进程结束，互拉停止 | ✅ |
| 9 | 二次启动 | — | 单实例，不重复顶栏 | ✅ |
| 10 | 无任务 | — | 不显示顶栏 | ✅ |
| 11 | 任务到期且 `behaviors` 含 `notify` | 到达 endTs | 触发一次系统通知 | ✅ |
| 11b | 任务到期（默认显示） | 到达 endTs | 该段保留为 100% 满色 | ✅ |
| 11c | 任务到期且含 `blink` | 到达 endTs | 柔和呼吸/色板过渡，持续到不再到期 | ✅ |
| 11d | ~~纯 `hide`~~ | — | **已废弃**（sanitize 过滤） | — |
| 12 | Headless 被结束 | Desktop 仍运行 | 数秒内 Headless 被 Desktop 拉起 | ✅ |
| 13 | 墙钟进度 | 休眠或长时间离开后恢复 | percent 按墙钟追上，不依赖本地累加 | ✅ |
| 14 | 用户选择"底边" | — | 进度条显示在屏幕底边 | ✅ |
| 15 | 用户选择"左边" | — | 进度条显示在屏幕左边，按方向填充 | ✅ |
| 16 | 用户启用"四边模式" | — | 四边同时显示进度条 | ✅ |
| 17 | 任务到期且含 `celebrate` | 到达 endTs | 四边同步呼吸（绝对时间对齐） | ✅ |
| 18 | 用户点击"完成"（循环任务） | — | 生成下一期并继续；原任务标完成 | ✅ |
| 19 | 用户点击"完成"（单次任务） | — | 标记已完成，不再渲染到桌面 | ✅ |
| 20 | 用户保存任务（颜色重复） | — | **拦截并提示**（现行仍禁止重复，非「确认后允许」） | ✅ |

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
2. 开始时间？**已决：定时任务必填 `startTs`；即时任务 `startTs = createdAt`** ✅
3. 配置文件是否允许用户手动编辑？__________（当前可直接改 `%APPDATA%\Hope\*.json`）
4. 是否需要 `hope-headless.exe --debug` 打开控制台？**已决：是** ✅ 已实现
5. 插件是否考虑上架 Microsoft Store？__________
6. 旧版 `需求文档.md`？**建议废弃，仅作历史参考**
7. 全局「暂停」是否冻结墙钟？**已决：不冻结；托盘暂停入口已收敛，墙钟始终权威** ✅
8. Desktop 拉起 Headless 时是否传 `--desktop` 完成双向互拉？**已完成（v0.4）**
9. 设置项是否并入配置窗体？**已完成（v0.4）**
10. IPC 细节维护何处？**已决：`docs/plugin-ipc.md` 为契约权威，本文 §5.2 摘要** ✅

---

## 附录 A：与旧版方案差异对照

| 旧版 | 新版 |
|------|------|
| Game Bar UWP 为唯一覆盖层 | Phase 1 改为 DWM 透明窗；Game Bar 降为插件备选 |
| 首版即覆盖独占全屏 | 独占全屏移至插件 Phase 2 |
| 三进程 + UWP 沙盒管道 | 首版简化 IPC，无 AppContainer ACL |
| 仅 Inno | **Inno + MSIX** 并行；商店托管签名 |
| `startAt`/`endAt` + 暂停累加 | Unix `startTs`/`endTs` 墙钟进度 |
| `pause`/`hide` IPC | 已移除；显隐由布局与 Overlay 生命周期决定 |

## 附录 B：参考链接

- [Fullscreen Optimizations（微软 DirectX 博客）](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)
- [Game Bar 点击穿透文档](https://learn.microsoft.com/en-us/gaming/game-bar/guide/click-through)
- [In-Game Overlays 原理（Fred Emmott）](https://fredemmott.com/blog/2022/05/31/in-game-overlays.html)
