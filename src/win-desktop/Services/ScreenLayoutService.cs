using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using Hope.Desktop.Interop;

namespace Hope.Desktop.Services;

/// <summary>
/// 描述主屏幕当前可用空间与任务栏状态，用于 Overlay 决定贴边/避让。
/// </summary>
public class ScreenLayoutInfo
{
    /// <summary>当前工作区（已扣除常驻任务栏占用），DIP 单位。</summary>
    public Rect WorkArea { get; set; }

    /// <summary>完整主屏幕矩形，DIP 单位。</summary>
    public Rect Bounds { get; set; }

    /// <summary>任务栏所在边：top / left / bottom / right / none。</summary>
    public string TaskbarEdge { get; set; } = "none";

    /// <summary>任务栏是否启用了自动隐藏。</summary>
    public bool TaskbarAutoHide { get; set; }

    /// <summary>主屏当前是否存在覆盖全屏的窗口（游戏、视频全屏等）。</summary>
    public bool HasFullScreenOnPrimary { get; set; }

    /// <summary>
    /// 获取进度条应使用的有效区域。
    /// 当任务栏自动隐藏或存在全屏应用时，返回完整屏幕 Bounds；否则返回 WorkArea。
    /// </summary>
    public Rect EffectiveArea(string barPosition)
    {
        bool useFullScreen = TaskbarAutoHide || HasFullScreenOnPrimary;
        return useFullScreen ? Bounds : WorkArea;
    }
}

/// <summary>
/// 主屏幕布局检测服务：提供任务栏位置、自动隐藏状态与全屏窗口检测。
/// </summary>
public static class ScreenLayoutService
{
    private const uint ABM_GETTASKBARPOS = 5;
    private const uint ABM_GETAUTOHIDEBAR = 7;

    // ABE_*
    private const uint ABE_LEFT = 0;
    private const uint ABE_TOP = 1;
    private const uint ABE_RIGHT = 2;
    private const uint ABE_BOTTOM = 3;

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    // 窗口样式
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_VISIBLE = 0x10000000;

    /// <summary>获取当前主屏布局信息（DIP 单位）。</summary>
    public static ScreenLayoutInfo GetCurrent()
    {
        var screen = Screen.PrimaryScreen;
        if (screen == null)
        {
            // 兜底：回退到 WPF 系统参数。
            var fallback = SystemParameters.WorkArea;
            return new ScreenLayoutInfo
            {
                WorkArea = fallback,
                Bounds = fallback,
                TaskbarEdge = "none",
                TaskbarAutoHide = false,
                HasFullScreenOnPrimary = false,
            };
        }

        double scaleX, scaleY;
        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
        {
            scaleX = g.DpiX / 96.0;
            scaleY = g.DpiY / 96.0;
        }

        var boundsPx = screen.Bounds;
        var workPx = screen.WorkingArea;

        var bounds = new Rect(
            boundsPx.X / scaleX,
            boundsPx.Y / scaleY,
            boundsPx.Width / scaleX,
            boundsPx.Height / scaleY);

        var workArea = new Rect(
            workPx.X / scaleX,
            workPx.Y / scaleY,
            workPx.Width / scaleX,
            workPx.Height / scaleY);

        string edge = DetectTaskbarEdge();
        bool autoHide = DetectAutoHide();
        bool fullScreen = DetectFullScreenOnPrimary(screen);

        return new ScreenLayoutInfo
        {
            WorkArea = workArea,
            Bounds = bounds,
            TaskbarEdge = edge,
            TaskbarAutoHide = autoHide,
            HasFullScreenOnPrimary = fullScreen,
        };
    }

    private static string DetectTaskbarEdge()
    {
        var abd = new NativeMethods.APPBARDATA { cbSize = Marshal.SizeOf(typeof(NativeMethods.APPBARDATA)) };
        IntPtr result = NativeMethods.SHAppBarMessage(ABM_GETTASKBARPOS, ref abd);
        if (result == IntPtr.Zero) return "none";

        return abd.uEdge switch
        {
            ABE_TOP => "top",
            ABE_BOTTOM => "bottom",
            ABE_LEFT => "left",
            ABE_RIGHT => "right",
            _ => "none",
        };
    }

    private static bool DetectAutoHide()
    {
        // 分别查询四条边是否有自动隐藏任务栏。
        for (uint edge = ABE_LEFT; edge <= ABE_BOTTOM; edge++)
        {
            var abd = new NativeMethods.APPBARDATA
            {
                cbSize = Marshal.SizeOf(typeof(NativeMethods.APPBARDATA)),
                uEdge = edge,
            };
            IntPtr hWnd = NativeMethods.SHAppBarMessage(ABM_GETAUTOHIDEBAR, ref abd);
            if (hWnd != IntPtr.Zero) return true;
        }
        return false;
    }

    private static bool DetectFullScreenOnPrimary(Screen screen)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        // 先判断窗口是否可见。
        int style = NativeMethods.GetWindowLong(fg, NativeMethods.GWL_STYLE);
        if ((style & WS_VISIBLE) == 0) return false;

        if (!NativeMethods.GetWindowRect(fg, out var rc)) return false;

        var boundsPx = screen.Bounds;
        // 允许 2 像素容差，覆盖微小边界差异。
        bool coversScreen =
            rc.Left <= boundsPx.Left + 2 &&
            rc.Top <= boundsPx.Top + 2 &&
            rc.Right >= boundsPx.Right - 2 &&
            rc.Bottom >= boundsPx.Bottom - 2;
        if (!coversScreen) return false;

        // 排除 Hope 自己的 Overlay 窗口：它们也是全屏、透明、穿透的。
        int exStyle = NativeMethods.GetWindowLong(fg, NativeMethods.GWL_EXSTYLE);
        bool isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        bool isNoActivate = (exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0;
        if (isToolWindow && isNoActivate) return false;

        // 带标题栏和可调边框的普通窗口通常不是全屏应用。
        bool hasCaption = (style & WS_CAPTION) != 0;
        bool hasThickFrame = (style & WS_THICKFRAME) != 0;
        if (hasCaption && hasThickFrame) return false;

        return true;
    }
}
