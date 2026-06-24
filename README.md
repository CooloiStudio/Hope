# Hope（盼头）

桌面效率提示工具。为多个任务设定时间与颜色，在屏幕顶端显示一根 `1~10px` 高的全宽**分段彩色**进度条；点击穿透、不抢焦点、不参与 Alt+Tab，唯一交互为悬停展示任务名。

> 完整设计与 **实现状态**见 [`Hope-产品与技术方案.md`](./Hope-产品与技术方案.md)（§0）。

## 架构（Phase 1）✅ 已实现

两个进程，互相监视拉起：

| 进程 | 技术栈 | 职责 |
|------|--------|------|
| `hope-headless.exe` | Go | 任务计时、墙钟实时进度计算、配置持久化、命名管道广播 |
| `hope-desktop.exe` | C# WPF | 配置窗体 + 系统托盘 + DWM 分段顶栏 Overlay |

通信：命名管道 `\\.\pipe\Hope\progress`，JSON Lines（详见方案文档 §5.2）。

## 目录结构

```
Hope/
├── src/
│   ├── headless/      # Go 核心（config / task / ipc / engine）
│   ├── win-desktop/   # WPF 桌面端（Overlay / Tray / Config / Ipc）
│   └── plugins/
│       └── fullscreen/  # Phase 2 全屏游戏插件（占位）
├── .github/workflows/release.yml
├── setup.iss          # Inno Setup 打包脚本
└── Hope-产品与技术方案.md
```

## 本地开发

前置：Go 1.26+、.NET 9 SDK（Windows x64）。

**调试：** 推荐用 **官方 VS Code** 打开本仓库并 F5（`.vscode/launch.json`）；Cursor 因 `vsdbg` 授权限制无法直接调试 .NET，可用 Cursor 写代码、VS Code 调试。

```powershell
# 1. 构建并启动 Headless 核心（调试模式带控制台日志）
cd src/headless
go build -o hope-headless.exe .
./hope-headless.exe --debug

# 2. 另开一个终端，运行 Desktop（会自动监视拉起 Headless）
cd src/win-desktop
dotnet run -c Release
```

Headless 也可由 Desktop 自动拉起；Desktop 会监视并重新启动 Headless。Headless → Desktop 方向互拉需 Headless 带 `--desktop <hope-desktop.exe 路径>` 启动（安装包场景待接线）。
数据与日志位于 `%APPDATA%\Hope`。

## 测试

```powershell
cd src/headless
go test ./...
```

## 适用范围

- ✅ 桌面、浏览器、Office、IDE
- ✅ 无边框全屏 / 窗口化 / 全屏优化（FSO）的游戏
- ❌ 真·独占全屏（需 Phase 2 全屏游戏拓展包）

## 许可

MIT
