# Hope 文档索引

本目录与根目录文档分工如下，避免多处「半真相」并存。

| 文档 | 角色 | 维护原则 |
|------|------|----------|
| [`../Hope-产品与技术方案.md`](../Hope-产品与技术方案.md) | **产品与架构权威**（实现状态、选型、进程模型、验收） | 随发版更新 §0 / §8 / §9；细节规格以代码为准时回写本节 |
| [`plugin-ipc.md`](./plugin-ipc.md) | **IPC 契约权威** | 与 `engine.HandleCommand` + `Ipc/Models.cs` 同步；方案文档 §5.2 与此对齐，冲突时以本文件 + 代码为准 |
| [`../README.md`](../README.md) | 对外简介、功能列表、下载入口 | 保持短；不复制架构细节 |
| [`../CHANGELOG.md`](../CHANGELOG.md) | 发版用户可见变更 | 按版本追加，不改写历史 |
| [`../privacy.md`](../privacy.md) | 隐私政策（商店 / 关于页） | 独立维护 |
| [`../packaging/README.md`](../packaging/README.md) | MSIX 打包与 Partner Center 身份 | 与 `pack-msix.ps1` 同步 |
| [`v0.7-新增功能规格.md`](./v0.7-新增功能规格.md) | **历史规格归档**（原 v0.8 设计稿） | 只读；实现状态见产品方案 §0 |
| [`allfour-analysis.md`](./allfour-analysis.md) | **历史算法核对笔记** | 只读；现行算法见 `engine.buildAllFourLayout` |
| [`../src/plugins/fullscreen/README.md`](../src/plugins/fullscreen/README.md) | Phase 2 占位 | 未实现前不扩写 |

## 版本号

| 组件 | 位置 | 规则 |
|------|------|------|
| Desktop | `src/win-desktop/Hope.Desktop.csproj` `<Version>` | patch 跨 minor 累加；发版 tag 与此一致 |
| Headless | `src/headless/main.go` `Version` | **独立递增**，不必等于 Desktop |

## 本地博客与自学笔记

技术博客选题与自学目录在 **`.blog/`**（已 gitignore，不入库）：

- `.blog/public/` — 对外博客选题（精简）
- `.blog/study/` — 个人技术复盘目录（完整）
