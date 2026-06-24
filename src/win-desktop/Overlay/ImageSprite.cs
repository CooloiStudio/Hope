using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Hope.Desktop.Interop;
using DrawingImage = System.Drawing.Image;
using WpfImage = System.Windows.Controls.Image;

namespace Hope.Desktop.Overlay;

/// <summary>
/// 一张挂在进度条下方、跟随进度前沿移动的图片精灵。
/// 支持任意图片格式；多帧动图（动画 GIF 等）由 ImageAnimator 驱动逐帧循环播放，
/// 每帧转换为 WPF 可显示的 BitmapSource。
/// </summary>
public sealed class ImageSprite : IDisposable
{
    public WpfImage Element { get; }
    public string Path { get; }
    public double Width { get; }
    public double Height { get; }

    private readonly Bitmap _bitmap;
    private readonly EventHandler _onFrame = (_, _) => { };
    private readonly bool _animating;
    private bool _disposed;

    public ImageSprite(string path, double maxHeight)
    {
        Path = path;
        _bitmap = (Bitmap)DrawingImage.FromFile(path);

        // 仅当高度超过 maxHeight 时等比缩小；否则保持原始尺寸。
        double scale = (_bitmap.Height > maxHeight && _bitmap.Height > 0)
            ? maxHeight / _bitmap.Height
            : 1.0;
        Height = Math.Max(1, _bitmap.Height * scale);
        Width = Math.Max(1, _bitmap.Width * scale);

        Element = new WpfImage
        {
            Width = Width,
            Height = Height,
            IsHitTestVisible = false,
            Stretch = System.Windows.Media.Stretch.Fill,
        };

        // 多帧图片（动画 GIF / 多帧 TIFF 等）才需要逐帧推进。
        if (ImageAnimator.CanAnimate(_bitmap))
        {
            ImageAnimator.Animate(_bitmap, _onFrame);
            _animating = true;
        }
        Render();
    }

    /// <summary>动图：推进到当前时间对应的帧并刷新显示。静态图无操作。</summary>
    public void Advance()
    {
        if (_disposed || !_animating) return;
        ImageAnimator.UpdateFrames(_bitmap);
        Render();
    }

    private void Render()
    {
        IntPtr h = _bitmap.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            Element.Source = src;
        }
        finally
        {
            NativeMethods.DeleteObject(h); // 避免 GDI 句柄泄漏
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_animating) ImageAnimator.StopAnimate(_bitmap, _onFrame);
        _bitmap.Dispose();
    }

    /// <summary>路径有效且文件存在时返回 true。</summary>
    public static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
