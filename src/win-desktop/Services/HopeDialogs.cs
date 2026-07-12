using System.Windows;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Hope.Desktop.Services;

/// <summary>
/// 配置窗确认对话框：WPF-UI Fluent MessageBox；关闭钮 / Esc /「取消」均视为取消。
/// </summary>
internal static class HopeDialogs
{
    /// <summary>显示确认框；仅主按钮返回 true。</summary>
    public static async Task<bool> ConfirmAsync(
        Window? owner,
        string title,
        string message,
        string primaryText = "确定",
        string closeText = "取消",
        bool dangerPrimary = false)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            IsPrimaryButtonEnabled = true,
            IsSecondaryButtonEnabled = false,
            IsCloseButtonEnabled = true,
            PrimaryButtonAppearance = dangerPrimary
                ? ControlAppearance.Danger
                : ControlAppearance.Primary,
        };
        if (owner != null)
            box.Owner = owner;

        var result = await box.ShowDialogAsync();
        return result == MessageBoxResult.Primary;
    }
}
