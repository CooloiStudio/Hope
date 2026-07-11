using System.Windows.Controls;
using Hope.Desktop.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class EditableTimeComboTests
{
    [Fact]
    public void ShouldIgnoreSelectionChange_WhenSelectedItemNull()
    {
        Assert.True(EditableTimeCombo.ShouldIgnoreSelectionChange(null));
        Assert.False(EditableTimeCombo.ShouldIgnoreSelectionChange("09"));
    }

    [Fact]
    public void Apply_WritesD2AndTryParseReadsBack()
    {
        RunOnSta(() =>
        {
            var box = CreateHourBox();
            var depth = 0;
            EditableTimeCombo.Apply(box, 9, ref depth);
            Assert.Equal(0, depth);
            Assert.Equal("09", box.Text);
            Assert.True(EditableTimeCombo.TryParse(box, 0, 23, out var n));
            Assert.Equal(9, n);
        });
    }

    [Fact]
    public void Normalize_ClampsOutOfRange()
    {
        RunOnSta(() =>
        {
            var box = CreateHourBox();
            box.Text = "99";
            var depth = 0;
            EditableTimeCombo.Normalize(box, 0, 23, ref depth);
            Assert.Equal("23", box.Text);
            Assert.Equal(0, depth);
        });
    }

    [Fact]
    public void Normalize_FallsBackToMinWhenEmpty()
    {
        RunOnSta(() =>
        {
            var box = CreateHourBox();
            box.Text = "";
            box.SelectedItem = null;
            var depth = 0;
            EditableTimeCombo.Normalize(box, 0, 23, ref depth);
            Assert.Equal("00", box.Text);
        });
    }

    [Fact]
    public void TryApplyFromSelection_IgnoresNullSelectedItem()
    {
        RunOnSta(() =>
        {
            var box = CreateHourBox();
            // 可编辑 ComboBox：SelectedItem=null 常伴随 Text 被清空；关键是不得再 Apply/Normalize。
            box.SelectedItem = null;
            var depth = 0;
            Assert.False(EditableTimeCombo.TryApplyFromSelection(box, 0, 23, ref depth));
            Assert.Equal(0, depth);
            Assert.Null(box.SelectedItem);
        });
    }

    [Fact]
    public void TryApplyFromSelection_UsesSelectedItemOverStaleText()
    {
        RunOnSta(() =>
        {
            var box = CreateHourBox();
            box.Text = "08";
            box.SelectedItem = "15";
            var depth = 0;
            Assert.True(EditableTimeCombo.TryApplyFromSelection(box, 0, 23, ref depth));
            Assert.Equal("15", box.Text);
        });
    }

    /// <summary>
    /// 回归：可编辑 ComboBox 写 Text 会触发 SelectionChanged(null)；若在 null 时 Normalize，会重入卡死 UI。
    /// 生产路径必须用 TryApplyFromSelection（忽略 null）+ Apply 的 depth 抑制。
    /// </summary>
    [Fact]
    public void ApplyWithSelectionHandler_DoesNotHangUiThread()
    {
        var done = new ManualResetEventSlim(false);
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                var box = CreateHourBox();
                var depth = 0;
                const int min = 0;
                const int max = 23;

                // 与 ConfigWindow.SetupEditableTimeCombo 一致的选中处理。
                box.SelectionChanged += (_, _) =>
                {
                    EditableTimeCombo.TryApplyFromSelection(box, min, max, ref depth);
                };

                EditableTimeCombo.Apply(box, 9, ref depth);
                EditableTimeCombo.Apply(box, 10, ref depth);
                box.SelectedItem = "11";
                EditableTimeCombo.Apply(box, 12, ref depth);
                EditableTimeCombo.Normalize(box, min, max, ref depth);

                Assert.Equal("12", box.Text);
                Assert.Equal(0, depth);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(2)),
            "UI thread hung applying editable time combo (likely SelectionChanged re-entrancy).");
        if (error != null)
            throw new InvalidOperationException("STA work failed", error);
    }

    /// <summary>
    /// 对照：旧错误策略（null 选中时 Normalize）在可编辑 ComboBox 上会挂死。
    /// 本断言锁定生产路径必须忽略 null SelectedItem。
    /// </summary>
    [Fact]
    public void BuggyNormalizeOnNullSelection_WouldHang_Documented()
    {
        // 不实际跑死循环：只断言生产策略与危险策略的差异点。
        Assert.True(EditableTimeCombo.ShouldIgnoreSelectionChange(null));
    }

    private static ComboBox CreateHourBox()
    {
        var box = new ComboBox { IsEditable = true };
        for (var i = 0; i < 24; i++)
            box.Items.Add(i.ToString("D2"));
        return box;
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "STA action timed out.");
        if (error != null)
            throw new InvalidOperationException("STA action failed", error);
    }
}
