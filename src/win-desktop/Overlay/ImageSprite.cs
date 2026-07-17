using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingImage = System.Drawing.Image;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using WpfImage = System.Windows.Controls.Image;

namespace Hope.Desktop.Overlay;

/// <summary>
/// 一张挂在进度条旁边、跟随进度前沿移动的图片精灵。
/// 支持任意图片格式；多帧动图（动画 GIF 等）由 ImageAnimator 驱动逐帧循环播放，
/// 每帧转换为 WPF 可显示的 BitmapSource。
/// 统一按高度限制尺寸，可通过 RotateTransform 旋转。
/// </summary>
public sealed class ImageSprite : IDisposable
{
    public WpfImage Element { get; }
    public string Path { get; }
    public double Width { get; }
    public double Height { get; }
    public double MaxSize { get; }

    private readonly Bitmap _bitmap;
    private readonly MemoryStream _stream;
    private readonly EventHandler _onFrame = (_, _) => { };
    private readonly bool _animating;
    private WriteableBitmap? _display;
    private bool _disposed;

    public ImageSprite(string path, double maxSize)
        : this(path, maxSize, File.ReadAllBytes(path))
    {
    }

    /// <summary>使用已读入内存的字节构造（避免在 UI 线程同步读盘）。</summary>
    public ImageSprite(string path, double maxSize, byte[] fileBytes)
    {
        Path = path;
        MaxSize = maxSize;
        // 将图片完整读入内存后再交给 GDI+：避免锁定原文件，防止更换图片时因句柄占用导致加载失败。
        _stream = new MemoryStream(fileBytes);
        _bitmap = (Bitmap)DrawingImage.FromStream(_stream);

        // 统一按高度限制尺寸，保持原始宽高比。
        double scale = 1.0;
        if (_bitmap.Height > maxSize && _bitmap.Height > 0)
            scale = maxSize / _bitmap.Height;

        Height = Math.Max(1, _bitmap.Height * scale);
        Width = Math.Max(1, _bitmap.Width * scale);

        Element = new WpfImage
        {
            Width = Width,
            Height = Height,
            IsHitTestVisible = false,
            Stretch = System.Windows.Media.Stretch.Fill,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new RotateTransform(0),
        };

        // 多帧图片（动画 GIF / 多帧 TIFF 等）才需要逐帧推进。
        if (ImageAnimator.CanAnimate(_bitmap))
        {
            ImageAnimator.Animate(_bitmap, _onFrame);
            _animating = true;
        }
        Render();
    }

    /// <summary>设置图片旋转角度（度）与旋转中心（相对于元素自身的比例坐标，默认 0.5,0.5）。</summary>
    public void SetRotation(double angle, double centerX = 0.5, double centerY = 0.5)
    {
        Element.RenderTransformOrigin = new System.Windows.Point(centerX, centerY);
        if (Element.RenderTransform is RotateTransform rt)
            rt.Angle = angle;
        else
            Element.RenderTransform = new RotateTransform(angle);
    }

    /// <summary>动图：推进到当前时间对应的帧并刷新显示。静态图无操作。</summary>
    public void Advance()
    {
        if (_disposed || !_animating) return;
        ImageAnimator.UpdateFrames(_bitmap);
        Render();
    }

    // 复用 WriteableBitmap 并 WritePixels，避免每帧 new BitmapSource + 替换 Source 造成 GC 与 UI 线程拥堵。
    private void Render()
    {
        int w = _bitmap.Width;
        int h = _bitmap.Height;
        if (w <= 0 || h <= 0) return;

        if (_display == null || _display.PixelWidth != w || _display.PixelHeight != h)
        {
            _display = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            Element.Source = _display;
        }

        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = _bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            _display.WritePixels(new Int32Rect(0, 0, w, h), data.Scan0, data.Stride * h, data.Stride);
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
        _display = null;
        _bitmap.Dispose();
        _stream.Dispose();
    }

    /// <summary>
    /// 后台探测缩放后尺寸（含读盘与 GDI 解码）；失败返回 null。
    /// 可在非 UI 线程调用。
    /// </summary>
    public static (double width, double height)? TryProbeScaledSize(string path, double maxSize)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            using var bitmap = (Bitmap)DrawingImage.FromStream(stream);
            double scale = 1.0;
            if (bitmap.Height > maxSize && bitmap.Height > 0)
                scale = maxSize / bitmap.Height;
            var h = Math.Max(1, bitmap.Height * scale);
            var w = Math.Max(1, bitmap.Width * scale);
            return (w, h);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>后台读入文件字节；失败返回 null。可在非 UI 线程调用。</summary>
    public static byte[]? TryReadAllBytes(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>路径有效且文件存在时返回 true。</summary>
    public static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
