using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Color = System.Drawing.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Hope.Desktop;

/// <summary>
/// 解析 src/resources 品牌图并生成应用/托盘图标（文档 §5.3 托盘图标）。
/// </summary>
internal static class AppIconHelper
{
    private const string ResourcesDir = "resources";

    /// <summary>深色系统主题下托盘用白色，浅色主题用黑色。</summary>
    public static bool IsDarkTheme()
    {
        try
        {
            var theme = ApplicationThemeManager.GetAppTheme();
            if (theme == ApplicationTheme.Dark) return true;
            if (theme == ApplicationTheme.Light) return false;
        }
        catch { /* 回退注册表 */ }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int useLight)
                return useLight == 0;
        }
        catch { /* 默认浅色 */ }

        return false;
    }

    /// <summary>托盘图标：hope-h.png 按主题着黑/白。</summary>
    public static Icon CreateTrayIcon()
    {
        var path = ResolveResource("hope-h.png");
        using var source = LoadBitmap(path);
        using var tinted = TintMonochrome(source, IsDarkTheme() ? Color.White : Color.Black);
        return ToIcon(tinted);
    }

    /// <summary>配置窗体标题栏图标：hope.png。</summary>
    public static void ApplyWindowIcon(Window window)
    {
        var path = ResolveResource("hope.png");
        if (!File.Exists(path)) return;
        try
        {
            window.Icon = BitmapFrame.Create(new Uri(path, UriKind.Absolute));
        }
        catch { /* 资源缺失时保留默认 */ }
    }

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

    /// <summary>将单色模板图（亮部=字形）着色为 foreground，暗部透明。</summary>
    private static Bitmap TintMonochrome(Bitmap source, Color foreground)
    {
        const int traySize = 32;
        using var scaled = new Bitmap(source, traySize, traySize);
        var result = new Bitmap(traySize, traySize, PixelFormat.Format32bppArgb);

        var srcData = scaled.LockBits(
            new Rectangle(0, 0, traySize, traySize), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = result.LockBits(
            new Rectangle(0, 0, traySize, traySize), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            for (int y = 0; y < traySize; y++)
            {
                for (int x = 0; x < traySize; x++)
                {
                    var c = Color.FromArgb(
                        System.Runtime.InteropServices.Marshal.ReadInt32(srcData.Scan0, y * srcData.Stride + x * 4));
                    double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    int alpha = (int)(c.A * Math.Clamp(lum / 255.0, 0, 1));
                    int packed = alpha < 8
                        ? 0
                        : Color.FromArgb(alpha, foreground.R, foreground.G, foreground.B).ToArgb();
                    System.Runtime.InteropServices.Marshal.WriteInt32(
                        dstData.Scan0, y * dstData.Stride + x * 4, packed);
                }
            }
        }
        finally
        {
            scaled.UnlockBits(srcData);
            result.UnlockBits(dstData);
        }

        return result;
    }

    private static Bitmap LoadBitmap(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("图标资源不存在", path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new Bitmap(fs);
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
