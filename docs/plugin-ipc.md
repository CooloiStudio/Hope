# Hope IPC 契约

Headless 与订阅方（Desktop；Phase 2 插件预留）之间的通信协议。架构上下文见根目录 `Hope-产品与技术方案.md` §5.2。

> **对齐代码**：`src/headless/ipc`、`src/headless/engine.HandleCommand`、`src/win-desktop/Ipc/Models.cs`（2026-07-13，Desktop `0.15.103` / Headless `0.10.28`）。

## 传输

- 命名管道：`\\.\pipe\Hope\progress`
- 编码：UTF-8，**JSON Lines**（每条消息一行，以 `\n` 结尾）
- 方向：双向（服务端广播 / 单播响应；客户端发命令）
- 写锁：按连接串行，避免广播与命令回包交错写坏帧

## 服务端 → 客户端：状态广播

定时按 `refreshSec` 广播；新连接立即推送最近一帧；`requestState` 立即按墙钟重算并单播。

```json
{
  "version": 1,
  "visible": true,
  "state": "running",
  "segments": [
    {
      "taskId": "uuid-1",
      "name": "晨会",
      "color": "#E53935",
      "gif": null,
      "imageMaxSize": 15,
      "barStart": 0.0,
      "barEnd": 30.0,
      "percent": 30.0,
      "fillEnd": 30.0,
      "endAt": "2026-07-13T18:00:00+08:00",
      "expired": false,
      "behaviors": null,
      "position": "top",
      "direction": "forward",
      "imageRotation": 0
    }
  ],
  "expired": [
    { "taskId": "uuid-9", "name": "交报告", "behaviors": ["notify"] }
  ]
}
```

| 字段 | 说明 |
|------|------|
| `visible` | 是否有可绘制段；无活跃/到期展示段时为 `false` |
| `state` | `idle` / `running` / `expired`（历史注释曾含 `paused`；托盘「暂停」已收敛，当前由无段/`visible` 表达隐藏） |
| `segments[]` | 单位置嵌套色段，或四边模式下带 `position`/`direction`/`imageRotation` 的周长映射段 |
| `expired[]` | 任务刚到期时**一次性**下发，供 `notify` 等；持续表现看段上的 `expired` + `behaviors` |

**进度公式（Unix 秒）：**  
`percent = clamp((nowTs - startTs) / (endTs - startTs) * 100, 0, 100)`；未开始任务不入段。

## 客户端 → 服务端：命令

| action | 主要字段 | 响应 | 说明 |
|--------|----------|------|------|
| `createTask` / `updateTask` | `task`，可选 `requestId` | `{"type":"tasks",...}` | 写后单播最新任务列表 |
| `deleteTask` | `taskId` | `tasks` | |
| `completeTask` | `taskId` | `tasks` | 循环任务先克隆下一期再标完成 |
| `deleteCompletedTasks` | — | `tasks` | |
| `listTasks` | 可选 `requestId` | `tasks` | |
| `getSettings` | 可选 `requestId` | `{"type":"settings",...}` | |
| `updateSettings` | `settings` | `settings` | 合并用户设置 |
| `screenSize` | `settings.screenWidth/Height` | — | **仅**上报屏幕尺寸，不走设置默认值合并 |
| `getVersion` | — | `{"type":"version","version":"..."}` | Headless 版本 |
| `requestState` | — | 一帧 `State` | 休眠唤醒等场景立即重算 |
| `quit` | — | `{"type":"quitAck"}` | 优雅退出，停互拉 |

> **已移除（勿再实现客户端调用）：** `pause` / `resume` / `hide` / `show`。顶栏显隐由布局结果与 Desktop Overlay 生命周期控制；「刷新进度条」走 Desktop 本地重置 + `requestState`。

### Task 结构（权威字段）

```json
{
  "id": "uuid",
  "name": "写文档",
  "type": "scheduled",
  "color": "#43A047",
  "gif": "C:\\path\\to\\follow.gif",
  "imageMaxSize": 0,
  "startTs": 1782189600,
  "endTs": 1782218400,
  "createdAt": "2026-07-13T09:30:00+08:00",
  "status": "active",
  "position": "",
  "expiredBehaviors": null,
  "recurrence": { "mode": "daily", "interval": 0, "weekdays": null }
}
```

- `startTs` / `endTs`：Unix 秒（权威）。旧 `startAt`/`endAt`（RFC3339）仅在时间戳缺失时读取，落盘迁移后移除。
- `type`：`scheduled` / `instant`（即时任务 `startTs` 取创建时刻）。
- `status`：`active` / `completed`；空值兼容旧 `completed` 布尔。
- `recurrence.mode`：`""` / `daily` / `everyN` / `weekly`。
- `expiredBehaviors`：可叠加 `blink` / `celebrate` / `notify`；`keep`/`hide` 已废弃（默认即到期仍显示）。

### Settings 要点

- 全局：`barHeightPx`、`imageMaxHeightPx`、`barPosition`、`barDirection`、`barDirections`、`allFour`、`expiredBehaviors`、`refreshSec`、`autostart`、`showConfigAtRuntime`、`autoUpdate`、`allowTelemetry` 等。
- `screenWidth` / `screenHeight`：由 `screenSize` 单独上报，供四边周长计算。
