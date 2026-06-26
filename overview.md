# 图片预览与桌面进度条修复概览

## 修复内容

### 1. 预览区 GIF 动图不播放
- **问题**：`ConfigWindow` 预览使用 `BitmapImage` 加载 GIF，WPF `Image` 默认只显示第一帧。
- **修复**：复用 `Overlay/ImageSprite.cs` 的 `ImageAnimator` 动画能力。预览区改为持有 `ImageSprite` 实例，并新增 100ms 的 `DispatcherTimer` 调用 `Advance()` 推进帧。
- **文件**：
  - `src/win-desktop/Views/ConfigWindow.xaml.cs`
  - `src/win-desktop/Overlay/ImageSprite.cs`（新增 `MaxHeight` 属性便于复用与尺寸比较）

### 2. 预览图片与进度条未紧贴
- **问题**：预览图片 `Canvas` 顶部有 `Margin="0,4,0,0"`，导致与进度条之间有 4px 空隙。
- **修复**：移除 `PreviewImageCanvas` 的上边距。
- **文件**：
  - `src/win-desktop/Views/ConfigWindow.xaml`

### 3. 调整最大尺寸保存后桌面图片尺寸未更新
- **问题**：`OverlayWindow.UpdateSprites()` 只在任务 ID 不存在或图片路径变化时才创建/替换精灵，已存在的精灵不会感知 `ImageMaxSize` 变化。
- **修复**：在精灵移除条件中追加 `seg.ImageMaxSize != sprite.MaxHeight` 判断，尺寸变化时立即释放旧精灵并重新创建。
- **文件**：
  - `src/win-desktop/Overlay/OverlayWindow.xaml.cs`
  - `src/win-desktop/Overlay/ImageSprite.cs`

## 验证

- `dotnet build src/win-desktop/Hope.Desktop.csproj -c Debug -o /z/hope/build-temp`：0 警告，0 错误。
- `cd src/headless && go test ./...`：通过。

> 注意：当前执行环境无法驻留 WPF 进程，实际播放/对齐效果需在 Windows 桌面端运行验证。
