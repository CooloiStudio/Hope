# Hope IPC 契约

Headless 与订阅方（Desktop、Phase 2 插件）之间的通信协议。详见方案文档 §5.2。

> **实现状态（2026-06-24）：** Headless 服务端与 Desktop 客户端均已实现下列契约；`hide`/`show`、`expired[]` 已合入。

## 传输

- 命名管道：`\\.\pipe\Hope\progress`
- 编码：UTF-8，**JSON Lines**（每条消息一行，以 `\n` 结尾）
- 方向：双向（服务端广播状态；客户端发送命令）

## 服务端 → 客户端：状态广播（每秒）

```json
{
  "version": 1,
  "visible": true,
  "state": "running",
  "timelineStart": "2026-06-23T08:00:00+08:00",
  "timelineEnd": "2026-06-23T18:00:00+08:00",
  "segments": [
    { "taskId": "uuid-1", "name": "晨会", "color": "#E53935", "barStart": 0.0, "barEnd": 30.0, "percent": 30.0, "fillEnd": 9.0 }
  ],
  "expired": [
    { "taskId": "uuid-9", "name": "交报告", "behavior": "notify" }
  ]
}
```

- `state`：`idle` / `running` / `paused` / `expired`
- `paused` 时 `visible=false`，但 `segments` 仍按墙钟更新（暂停不冻结墙钟）
- `expired[]`：任务到期时一次性下发，供执行 `expiredBehavior`

## 客户端 → 服务端：命令

| action | 字段 | 说明 |
|--------|------|------|
| `createTask` | `task` | 新建任务 |
| `updateTask` | `task` | 按 `task.id` 更新 |
| `deleteTask` | `taskId` | 删除任务 |
| `listTasks` | — | 请求当前任务列表（服务端回 `{"type":"tasks","tasks":[...]}`） |
| `updateSettings` | `settings` | 更新设置 |
| `pause` / `resume` | — | 全局暂停 / 恢复（仅影响 `visible`） |
| `hide` / `show` | — | 隐藏 / 显示顶栏（不影响墙钟计算） |
| `quit` | — | 通知 Headless 优雅退出 |

### Task 结构

```json
{
  "id": "uuid",
  "name": "写文档",
  "type": "scheduled",
  "color": "#43A047",
  "gif": "C:\\path\\to\\follow.gif",
  "startAt": "2026-06-23T10:00:00+08:00",
  "endAt": "2026-06-23T18:00:00+08:00",
  "createdAt": "2026-06-23T09:30:00+08:00"
}
```

- `type`：`scheduled`（需 `startAt`）/ `instant`（`startAt` 取 `createdAt`）
- `gif`：可选；本地**图片/动图**路径（字段名历史保留），Overlay 在进度条下方跟随 `fillEnd` 显示；动图循环播放
- `percent = clamp((now - startAt) / (endAt - startAt) * 100, 0, 100)`
