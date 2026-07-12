using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Hope.Desktop.Services;

/// <summary>
/// 监听 Shell / DWM 相关消息，用于 Overlay 透明失效后的自动重建
/// （explorer 重启 → TaskbarCreated；合成变化 / UAC 返回 → WM_DWMCOMPOSITIONCHANGED）。
/// </summary>
internal sealed class ShellCompositionWatcher : NativeWindow, IDisposable
{
    public const int WmDwmCompositionChanged = 0x031E;

    private readonly uint _taskbarCreated;
    private readonly Action _onShellOrDwmChanged;
    private bool _disposed;

    public ShellCompositionWatcher(Action onShellOrDwmChanged)
    {
        _onShellOrDwmChanged = onShellOrDwmChanged;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        CreateHandle(new CreateParams
        {
            Caption = "Hope.ShellCompositionWatcher",
            Parent = new IntPtr(-3), // HWND_MESSAGE
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (!_disposed &&
            (m.Msg == WmDwmCompositionChanged ||
             (_taskbarCreated != 0 && m.Msg == (int)_taskbarCreated)))
        {
            _onShellOrDwmChanged();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyHandle();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);
}
