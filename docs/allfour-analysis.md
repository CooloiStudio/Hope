# 四边环绕实现分析报告（历史核对笔记）

> **归档说明**：本文为开发期核对笔记，用于对照 `deriveSurroundDir` / 边序 / 本地方向。  
> **现行实现**以 `src/headless/engine/engine.go`（`buildAllFourLayout` 等）与 Desktop Overlay 为准；若与本文冲突，以代码为准。  
> 产品层说明见 [`Hope-产品与技术方案.md`](../Hope-产品与技术方案.md)；IPC 见 [`plugin-ipc.md`](./plugin-ipc.md)。

---

## 一、当前实现概述

### 1.1 方向推断 `deriveSurroundDir`
根据起点位置 + 填充方向判断环绕方向（顺时针/逆时针）：

| 起点 | forward | reverse |
|------|---------|---------|
| top | cw | ccw |
| bottom | ccw | cw |
| left | ccw | cw |
| right | cw | ccw |

**结论：与用户给出的方向推断表完全一致 ✓**

---

### 1.2 边顺序推导 `deriveAllFourOrders`
先调用 `deriveSurroundDir` 得到环绕方向，再选择对应的基础队列：
- 顺时针：`[top, right, bottom, left]`
- 逆时针：`[top, left, bottom, right]`

然后将队列旋转，使 `startPos` 位于 `[0]`。

**示例验证：**
- `top+forward`（顺时针）：baseOrder=`[top,right,bottom,left]`，旋转后=`[top,right,bottom,left]` ✓
- `top+reverse`（逆时针）：baseOrder=`[top,left,bottom,right]`，旋转后=`[top,left,bottom,right]` ✓
- `bottom+forward`（逆时针）：baseOrder=`[top,left,bottom,right]`，旋转后=`[bottom,right,top,left]`
  - 验证：逆时针从 bottom 出发，顺序应为 bottom→right→top→left ✓
- `bottom+reverse`（顺时针）：baseOrder=`[top,right,bottom,left]`，旋转后=`[bottom,left,top,right]`
  - 验证：顺时针从 bottom 出发，顺序应为 bottom→left→top→right ✓

**结论：边顺序推导正确 ✓**

---

### 1.3 本地填充方向 `localDirection`
根据环绕方向，为每条边返回正确的本地填充方向：
- 顺时针：top=forward, right=forward, bottom=reverse, left=reverse
- 逆时针：top=reverse, left=forward, bottom=forward, right=reverse

**结论：正确 ✓**

---

### 1.4 图片旋转 `rotationForSide`
当前实现：
- top → 0°（图片顶部吸附进度条）
- right → 0°（图片水平放置于进度条旁）
- bottom → 180°（图片底部吸附进度条）
- left → 0°（图片水平放置于进度条旁）

**问题：left 和 right 返回 0°，意味着图片始终是水平放置。**
但根据用户之前的要求，left/right 时图片应紧贴进度条，当前实现已改为水平放置（0°），
窗口宽度 = 进度条粗细 + 图片展示宽度。这部分逻辑在 `OverlayWindow.xaml.cs` 中，engine 层的 `ImageRotation` 字段当前对 left/right 返回 0°，与 Desktop 层逻辑一致。

**结论：与当前 Desktop 层实现一致，暂无明显错误**

---

### 1.5 进度计算 `buildAllFourLayout`
- 用物理周长 `(w+h)*2` 计算进度位置
- `cum[i]` 按实际 `sides` 顺序动态计算，不硬编码 ✓
- 已填满的边生成满段（不挂图片），活跃边挂图片 ✓
- 未到达的边不生成 Segment ✓

**结论：逻辑正确 ✓**

---

## 二、用户新算法与当前实现的对比

### 2.1 用户算法描述
```
1. 新建队列，固定初始化为：[顶、右、下、左]
2. 调整队列：读取队首，若与起点不一致，则出队并重新入队（旋转）
3. 按调整后队列安排展示
```

### 2.2 问题：固定初始化队列不支持逆时针

用户算法中**队列始终初始化为 `[顶、右、下、左]`（顺时针顺序）**，
但逆时针时基础队列应为 `[顶、左、下、右]`。

**举例：top+reverse（逆时针）**
- 用户算法：队列=`[top,right,bottom,left]`，队首=top，一致，结果=`[top,right,bottom,left]`
- 正确结果：`[top,left,bottom,right]`
- **不一致！** 用户算法对逆时针情况给出了错误的边顺序。

### 2.3 修正后的等价算法

用户算法的精神（固定基础队列 + 旋转定位起点）是正确的，但需根据环绕方向选择不同的基础队列：

```
1. 根据环绕方向选择基础队列：
   - 顺时针：[顶、右、下、左]
   - 逆时针：[顶、左、下、右]
2. 旋转队列，使起点位于队首
3. 按队列顺序安排各边
```

**这与当前 `deriveAllFourOrders` 的实现完全一致。**

---

## 三、发现的潜在问题

### 3.1 `deriveAllFourOrders` 中 baseOrder 选择逻辑 ✓ 已正确
当前代码：
```go
if surroundDir == "cw" {
    baseOrder = []string{"top", "right", "bottom", "left"}
} else {
    baseOrder = []string{"top", "left", "bottom", "right"}
}
```
与实际环绕方向匹配，正确。

### 3.2 `localDirection` 正确性验证 ✓
以 `bottom+forward`（逆时针）为例：
- sides = `[bottom, right, top, left]`
- `localDirection("bottom", "ccw")` → `bottom` 在 ccw 基础顺序 `[top,left,bottom,right]` 中索引为 2
  - ccw: top=reverse, left=forward, bottom=forward, right=reverse
  - 所以 bottom → forward ✓（逆时针从 bottom 出发，bottom 边从左到右填充）

实际验证代码逻辑：
```go
// localDirection 中 ccw 分支：
case "bottom": return "forward"  ✓
```

### 3.3 `rotationForSide` 问题 △ 需确认

当前 `rotationForSide` 对 `left` 返回 0°，这意味着图片在 left 位置时旋转 0°（水平放置）。

根据之前用户的要求（图片紧贴进度条），left/right 时图片应水平放置，
旋转中心设为吸附边中点。当前 Desktop 层 `OverlayWindow.xaml.cs` 已按此实现。

**但 `rotationForSide` 的返回值当前对 left/right 均为 0°，**
而 top=0°, bottom=180°，这个映射是否正确的关键是：**Desktop 层是否根据 `ImageRotation` 字段来旋转图片。**

查看 Desktop 层代码，`ImageRotation` 字段未被使用（图片旋转逻辑在 Desktop 层直接计算），
所以 `rotationForSide` 的返回值目前不影响显示效果。

**建议：如果 Desktop 层将来使用 `ImageRotation` 字段，需确保值正确。**
当前 left/right 返回 0° 与 Desktop 层实现一致，暂时没有问题。

---

## 四、与用户算法的等价性结论

| 步骤 | 用户算法 | 当前实现 | 是否等价 |
|------|---------|---------|---------|
| 方向推断 | 查表 | `deriveSurroundDir` | ✓ 等价 |
| 边顺序 | 固定队列 `[顶右上左]` + 旋转 | `deriveAllFourOrders`（根据方向选队列 + 旋转） | ✗ 用户算法缺少逆时针队列 |
| 本地方向 | 未说明 | `localDirection` | 当前实现完整 |
| 图片旋转 | 未说明 | `rotationForSide` | 当前实现存在但未使用 |

**核心结论：用户算法需修正为"根据环绕方向选择基础队列"，修正后与当前实现等价。**

---

## 五、建议

### 5.1 用户算法修正
将步骤 1 改为：
```
1. 根据环绕方向选择基础队列：
   - 顺时针：[顶、右、下、左]
   - 逆时针：[顶、左、下、右]
2. 旋转队列使起点位于队首
```

### 5.2 当前代码无需修改
当前 `deriveSurroundDir` + `deriveAllFourOrders` 的实现与用户修正后的算法等价，
方向推断也完全匹配用户给出的表格。

**如果实际显示效果仍不正确，问题可能在：**
1. Desktop 层 `OverlayWindow.xaml.cs` 的图片定位逻辑
2. 测试用例覆盖不足，某些边界情况未考虑到

### 5.3 建议增加的测试
- `TestDeriveAllFourOrders` 已覆盖 8 种组合 ✓
- 建议增加集成测试：给定具体任务，验证生成的 Segments 的 Position、Direction、BarStart/BarEnd 是否符合预期
- 建议在 Desktop 层增加可视化调试（如把各边 Segment 信息输出到日志）

---

## 六、总结

| 项目 | 状态 | 说明 |
|------|------|------|
| 方向推断 | ✓ 正确 | 与用户表格完全一致 |
| 边顺序推导 | ✓ 正确 | 根据方向选择队列，旋转定位起点 |
| 本地填充方向 | ✓ 正确 | 每条边的填充方向与环绕方向匹配 |
| 进度计算 | ✓ 正确 | cum 动态计算，活跃/已满/未到逻辑正确 |
| 用户算法 | △ 需修正 | 固定队列不支持逆时针，需根据方向选择基础队列 |

**当前实现逻辑正确，与用户修正后的算法等价。如实际效果仍不正确，建议排查 Desktop 层图片定位代码或增加集成测试。**
