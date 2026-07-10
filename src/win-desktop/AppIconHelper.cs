using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Drawing.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Hope.Desktop;

/// <summary>
/// 解析 src/resources 品牌图并生成应用/托盘图标（文档 §5.3 托盘图标）。
/// </summary>
internal static class AppIconHelper
{
    private const string ResourcesDir = "resources";
    private const int TraySizePx = 32;

    /// <summary>
    /// 托盘图标：直接使用 hope-mini.png（白底黑字），不做主题着色。
    /// </summary>
    public static Icon CreateTrayIcon()
    {
        var path = ResolveResource("hope-mini.png");
        using var source = LoadBitmap(path);
        using var scaled = ResizeHighQuality(source, TraySizePx, TraySizePx);
        return ToIcon(scaled);
    }

    /// <summary>任务栏/窗口标题栏图标：多尺寸 hope.ico 中选最接近目标像素的一帧；回退 hope-mini / hope.png。</summary>
    public static ImageSource? LoadAppChromeIconSource(int preferPx = 32)
    {
        var icoPath = ResolveResource("hope.ico");
        if (File.Exists(icoPath))
        {
            try
            {
                using var stream = File.OpenRead(icoPath);
                var decoder = BitmapDecoder.Create(
                    stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame? best = null;
                var bestDiff = int.MaxValue;
                foreach (var frame in decoder.Frames)
                {
                    var diff = Math.Abs(frame.PixelWidth - preferPx);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        best = frame;
                    }
                }
                if (best != null)
                {
                    best.Freeze();
                    return best;
                }
            }
            catch { /* 回退 PNG */ }
        }

        var pngName = preferPx >= 256 ? "hope.png" : "hope-mini.png";
        var pngPath = ResolveResource(pngName);
        if (!File.Exists(pngPath))
            pngPath = ResolveResource("hope.png");
        if (!File.Exists(pngPath)) return null;
        try
        {
            var uri = new Uri(pngPath, UriKind.Absolute);
            var frame = BitmapFrame.Create(uri);
            frame.Freeze();
            return frame;
        }
        catch { return null; }
    }

    /// <summary>窗口图标：使用完整 hope.ico（多尺寸），供标题栏与 Alt+Tab。</summary>
    public static void ApplyWindowIcon(Window window)
    {
        var icoPath = ResolveResource("hope.ico");
        if (File.Exists(icoPath))
        {
            try
            {
                window.Icon = BitmapFrame.Create(new Uri(icoPath, UriKind.Absolute));
                return;
            }
            catch { /* 回退 */ }
        }

        var source = LoadAppChromeIconSource(32);
        if (source == null) return;
        try { window.Icon = source; }
        catch { /* 资源缺失时保留默认 */ }
    }

    /// <summary>WPF-UI TitleBar 小图标：从 hope.ico 取最接近 16px 的帧。</summary>
    public static void ApplyTitleBarIcon(Wpf.Ui.Controls.TitleBar titleBar)
    {
        var source = LoadAppChromeIconSource(16);
        if (source == null) return;
        titleBar.Icon = new Wpf.Ui.Controls.ImageIcon
        {
            Source = source,
            Width = 16,
            Height = 16,
        };
    }

    public static ImageSource? LoadHopeImageSource() => LoadAppChromeIconSource(32);

    public static string ResolveResource(string fileName)
    {
        var prod = Path.Combine(AppContext.BaseDirectory, ResourcesDir, fileName);
        if (File.Exists(prod)) return prod;

        // 开发：Monorepo 下 src/resources（与 HeadlessSupervisor 同级回退层数）。
        var dev = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ResourcesDir, fileName));
        if (File.Exists(dev)) return dev;

        return prod;
    }

    private static Bitmap LoadBitmap(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("图标资源不存在", path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new Bitmap(fs);
    }

    private static Bitmap ResizeHighQuality(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        using (var temp = Icon.FromHandle(bitmap.GetHicon()))
            temp.Save(ms);
        ms.Position = 0;
        return new Icon(ms);
    }
}
