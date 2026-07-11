using System.Linq;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;

namespace Hope.Desktop.Services;

/// <summary>
/// 可编辑时/分 ComboBox 的读写策略。
/// 写 Text 会短暂清空 SelectedItem；若在 null 选中时再 Normalize/Apply，会重入卡死 UI 线程，
/// 导致 Dispatcher 无法处理 IPC 水合（表现为启动无进度条、设置空白）。
/// </summary>
public static class EditableTimeCombo
{
    /// <summary>写入 Text 清空选中时 SelectionChanged 会带 null，必须忽略。</summary>
    public static bool ShouldIgnoreSelectionChange(object? selectedItem) => selectedItem == null;

    /// <summary>
    /// 读取当前输入：优先 Text（含正在输入），空则回退 SelectedItem。
    /// </summary>
    public static bool TryReadRaw(ComboBox box, out string raw)
    {
        raw = (box.Text ?? "").Trim();
        if (raw.Length == 0 && box.SelectedItem != null)
            raw = box.SelectedItem.ToString()?.Trim() ?? "";
        return raw.Length > 0;
    }

    /// <summary>解析并钳制到 [min, max]。</summary>
    public static bool TryParse(ComboBox box, int min, int max, out int value)
    {
        value = min;
        if (!TryReadRaw(box, out var raw)) return false;
        if (!int.TryParse(raw, out var n)) return false;
        value = Math.Clamp(n, min, max);
        return true;
    }

    /// <summary>
    /// 将值写入 ComboBox（D2）。通过 <paramref name="applyDepth"/> 抑制嵌套 SelectionChanged/TextChanged。
    /// 先选中 Items 中的项，再同步 Text，减少可编辑模式下 SelectedItem 被清空的抖动。
    /// </summary>
    public static void Apply(ComboBox box, int value, ref int applyDepth)
    {
        var text = value.ToString("D2");
        applyDepth++;
        try
        {
            var item = box.Items.Cast<object>().FirstOrDefault(i => Equals(i?.ToString(), text));
            if (item != null)
            {
                if (!ReferenceEquals(box.SelectedItem, item) && !Equals(box.SelectedItem, item))
                    box.SelectedItem = item;
            }
            else if (!Equals(box.SelectedItem, text))
            {
                box.SelectedItem = text;
            }

            if (box.Text != text)
                box.Text = text;
        }
        finally
        {
            applyDepth--;
        }
    }

    /// <summary>按当前输入钳制；无法解析时回退到 min。</summary>
    public static void Normalize(ComboBox box, int min, int max, ref int applyDepth)
    {
        if (!TryReadRaw(box, out var raw) || !int.TryParse(raw, out var n))
        {
            Apply(box, min, ref applyDepth);
            return;
        }
        Apply(box, Math.Clamp(n, min, max), ref applyDepth);
    }

    /// <summary>
    /// 下拉选中时的处理：忽略 null / 无法解析；否则以 SelectedItem 为准 Apply。
    /// 返回是否实际写入（便于调用方决定是否自动保存）。
    /// </summary>
    public static bool TryApplyFromSelection(ComboBox box, int min, int max, ref int applyDepth)
    {
        if (applyDepth > 0) return false;
        if (ShouldIgnoreSelectionChange(box.SelectedItem)) return false;
        var raw = box.SelectedItem!.ToString()?.Trim() ?? "";
        if (!int.TryParse(raw, out var n)) return false;
        Apply(box, Math.Clamp(n, min, max), ref applyDepth);
        return true;
    }
}
