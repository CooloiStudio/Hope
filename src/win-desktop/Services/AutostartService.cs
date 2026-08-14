using Hope.Desktop.Ipc;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Hope.Desktop.Services;

public enum AutostartApplyResult
{
    Applied,
    /// <summary>商店版：用户曾在系统设置里关闭启动项，应用无法自行打开。</summary>
    DisabledByUser,
    Failed,
}

/// <summary>
/// 开机自启：侧载写 HKCU Run；商店/MSIX 走清单中的 windows.startupTask。
/// </summary>
public static class AutostartService
{
    public const string StartupTaskId = "HopeStartup";
    public const string RunValueName = "Hope";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static async Task<AutostartApplyResult> ApplyAsync(bool enable)
    {
        try
        {
            if (InstallChannel.IsStoreManaged)
                return await ApplyStoreAsync(enable).ConfigureAwait(true);

            ApplyRegistry(enable);
            return AutostartApplyResult.Applied;
        }
        catch (Exception ex)
        {
            DesktopLog.Warn($"Autostart apply failed enable={enable}: {ex.Message}");
            return AutostartApplyResult.Failed;
        }
    }

    /// <summary>系统当前是否已启用开机自启（侧载看注册表，商店看 StartupTask）。</summary>
    public static async Task<bool> IsOsEnabledAsync()
    {
        try
        {
            if (InstallChannel.IsStoreManaged)
            {
                var task = await StartupTask.GetAsync(StartupTaskId);
                return task.State == StartupTaskState.Enabled;
            }

            return IsRegistryEnabled();
        }
        catch (Exception ex)
        {
            DesktopLog.Warn($"Autostart IsOsEnabled failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 首次读到设置时对齐：侧载以注册表为准（安装程序可能已写入）；
    /// 商店版以配置为准，向 StartupTask 对齐（不写 Run 键）。
    /// </summary>
    public static async Task ReconcileFromSettingsAsync(SettingsDto settings, Action<SettingsDto> persist)
    {
        if (InstallChannel.IsStoreManaged)
        {
            var result = await ApplyAsync(settings.Autostart).ConfigureAwait(true);
            DesktopLog.Info($"Autostart store reconcile want={settings.Autostart} result={result}");
            if (settings.Autostart && result == AutostartApplyResult.DisabledByUser)
            {
                settings.Autostart = false;
                persist(settings);
            }
            return;
        }

        bool regOn = IsRegistryEnabled();
        if (regOn == settings.Autostart) return;

        DesktopLog.Info($"Autostart reconcile: registry={regOn} config={settings.Autostart} → 以注册表为准对齐配置");
        settings.Autostart = regOn;
        persist(settings);
    }

    public static bool IsRegistryEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyRegistry(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key == null) return;
        if (enable)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) key.SetValue(RunValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private static async Task<AutostartApplyResult> ApplyStoreAsync(bool enable)
    {
        var task = await StartupTask.GetAsync(StartupTaskId);
        DesktopLog.Info($"Autostart store task state={task.State} enable={enable}");

        if (!enable)
        {
            if (task.State == StartupTaskState.Enabled)
                task.Disable();
            return AutostartApplyResult.Applied;
        }

        switch (task.State)
        {
            case StartupTaskState.Enabled:
                return AutostartApplyResult.Applied;
            case StartupTaskState.Disabled:
            case StartupTaskState.DisabledByPolicy:
                var after = await task.RequestEnableAsync();
                DesktopLog.Info($"Autostart store RequestEnable → {after}");
                if (after == StartupTaskState.Enabled) return AutostartApplyResult.Applied;
                if (after == StartupTaskState.DisabledByUser) return AutostartApplyResult.DisabledByUser;
                return AutostartApplyResult.Failed;
            case StartupTaskState.DisabledByUser:
                return AutostartApplyResult.DisabledByUser;
            default:
                return AutostartApplyResult.Failed;
        }
    }
}
