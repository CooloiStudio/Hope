using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingImage = System.Drawing.Image;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
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
    public double MaxHeight { get; }
    public bool LimitWidth { get; }

    private readonly Bitmap _bitmap;
    private readonly MemoryStream _stream;
    private readonly EventHandler _onFrame = (_, _) => { };
    private readonly bool _animating;
    private bool _disposed;

    public ImageSprite(string path, double maxSize, bool limitWidth = false)
    {
        Path = path;
        LimitWidth = limitWidth;
        MaxHeight = limitWidth ? 0 : maxSize;
        // 将图片完整读入内存后再交给 GDI+：避免锁定原文件，防止更换图片时因句柄占用导致加载失败。
        var bytes = File.ReadAllBytes(path);
        _stream = new MemoryStream(bytes);
        _bitmap = (Bitmap)DrawingImage.FromStream(_stream);

        // 仅当尺寸超过上限时等比缩小；否则保持原始尺寸。
        // 水平边限制高度，垂直边限制宽度。
        double scale = 1.0;
        if (limitWidth && _bitmap.Width > maxSize && _bitmap.Width > 0)
            scale = maxSize / _bitmap.Width;
        else if (!limitWidth && _bitmap.Height > maxSize && _bitmap.Height > 0)
            scale = maxSize / _bitmap.Height;

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

    // 用 LockBits → Bgra32 转换，保留 PNG/GIF 的 alpha 透明通道。
    // （GetHbitmap 会丢失 alpha，把透明区域填成不透明底色，导致顶栏出现“背景色”。）
    private void Render()
    {
        var rect = new Rectangle(0, 0, _bitmap.Width, _bitmap.Height);
        BitmapData data = _bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(
                _bitmap.Width, _bitmap.Height, 96, 96,
                PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * _bitmap.Height, data.Stride);
            src.Freeze();
            Element.Source = src;
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_animating) ImageAnimator.StopAnimate(_bitmap, _onFrame);
        _bitmap.Dispose();
        _stream.Dispose();
    }

    /// <summary>路径有效且文件存在时返回 true。</summary>
    public static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
