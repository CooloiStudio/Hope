# Hope 全屏游戏拓展包（Phase 2，占位）

> 本目录为 Phase 2 预留，**首版不实现**。

## 目标

让顶栏进度条在 **真·独占全屏（Exclusive Fullscreen）** 游戏中也可见。Phase 1 的 DWM 透明窗在独占全屏下会被绕过（游戏直接写显存，跳过 DWM 合成）。

## 形态

- 独立安装包 `Hope.Plugin.Fullscreen_Setup.exe`，可选安装，独立版本号。
- 安装前强制展示反作弊与兼容性风险提示。

## 技术方向（预研，详见方案文档 §4.3 / §6）

| 方案 | 适用 | 风险 |
|------|------|------|
| DXGI `Present` Hook 绘制顶栏 | 真独占全屏 | 反作弊、多 overlay 冲突 |
| 显示模式助手（强制无边框） | 可改无边框的游戏 | 低 |
| Xbox Game Bar Widget | 部分 FSO | 需用户 Pin |

## 与核心包的关系

- 订阅与 Desktop 相同的 Headless 广播管道 `\\.\pipe\Hope\progress`（只读）。
- 不负责任务 CRUD 与计时（仍由 Headless 负责）。
- Hook 绘制成功时，Desktop 侧 DWM 顶栏隐藏，避免双线重叠（协调字段 `presenter`，预留）。

## 预研任务清单

- [ ] DXGI Present Hook POC（DX11 单后端）
- [ ] Top 50 Steam 游戏呈现模式分布
- [ ] EAC / BattlEye / Vanguard 下行为记录（不承诺零风险）
- [ ] 与 RTSS / Steam Overlay 共存策略
- [ ] 代码签名方案
