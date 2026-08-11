using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Hope.Desktop.Services;

/// <summary>
/// 区分安装渠道：Inno 自发布（侧载） vs MSIX 商店包。
/// 商店版更新由 Microsoft Store 托管，应用内仅提示新版本并引导打开商店。
/// </summary>
public static class InstallChannel
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppmodelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, StringBuilder packageFamilyName);

    /// <summary>当前进程是否以 MSIX 包身份运行（含 Microsoft Store 安装）。</summary>
    public static bool IsStoreManaged => TryGetPackageFamilyName(out _);

    public static bool TryGetPackageFamilyName(out string familyName)
    {
        familyName = "";
        var sb = new StringBuilder(256);
        uint len = (uint)sb.Capacity;
        int hr = GetCurrentPackageFamilyName(ref len, sb);
        if (hr == AppmodelErrorNoPackage) return false;
        if (hr == ErrorInsufficientBuffer && len > 0)
        {
            sb = new StringBuilder((int)len);
            hr = GetCurrentPackageFamilyName(ref len, sb);
        }
        if (hr != 0) return false;
        familyName = sb.ToString();
        return !string.IsNullOrWhiteSpace(familyName);
    }

    /// <summary>
    /// MSIX 将包进程对 %APPDATA%（Roaming）的写入重定向到
    /// %LOCALAPPDATA%\Packages\{PFN}\LocalCache\Roaming。
    /// 该物理路径供资源管理器等包外进程打开；侧载版返回 null。
    /// </summary>
    public static string? TryGetVirtualizedRoamingRoot()
    {
        if (!TryGetPackageFamilyName(out var pfn)) return null;
        return BuildVirtualizedRoamingRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            pfn);
    }

    /// <summary>拼出商店包虚拟化后的 Roaming 根目录（可单测）。</summary>
    public static string BuildVirtualizedRoamingRoot(string localAppData, string packageFamilyName) =>
        Path.Combine(localAppData, "Packages", packageFamilyName, "LocalCache", "Roaming");

    /// <summary>
    /// 把逻辑 Roaming 下的相对路径解析为资源管理器可打开的物理目录。
    /// 优先已存在的虚拟化目录；若真实 Roaming 已存在（侧载残留）则回退到真实路径。
    /// </summary>
    public static string ResolveExplorerPathUnderRoaming(params string[] relativeSegments)
    {
        var logical = Path.Combine(
            new[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) }
                .Concat(relativeSegments)
                .ToArray());

        var virtualizedRoaming = TryGetVirtualizedRoamingRoot();
        if (virtualizedRoaming == null)
            return logical;

        var physical = Path.Combine(
            new[] { virtualizedRoaming }.Concat(relativeSegments).ToArray());

        if (Directory.Exists(physical)) return physical;
        if (Directory.Exists(logical)) return logical;
        return physical;
    }

    /// <summary>打开本应用在 Microsoft Store 的产品页（按 Package Family Name）。</summary>
    public static bool TryOpenMicrosoftStore()
    {
        try
        {
            var url = TryGetPackageFamilyName(out var pfn)
                ? $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(pfn)}"
                : "ms-windows-store://home";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            DesktopLog.Warn($"InstallChannel: open store failed: {ex.Message}");
            return false;
        }
    }
}
