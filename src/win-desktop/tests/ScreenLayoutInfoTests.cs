using System.Windows;
using Hope.Desktop.Services;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class ScreenLayoutInfoTests
{
    private static ScreenLayoutInfo Layout(
      bool autoHide,
      bool fullScreen,
      string taskbarEdge = "bottom") =>
      new()
      {
          WorkArea = new Rect(0, 0, 1900, 1040),
          Bounds = new Rect(0, 0, 1920, 1080),
          TaskbarEdge = taskbarEdge,
          TaskbarAutoHide = autoHide,
          HasFullScreenOnPrimary = fullScreen,
      };

    [Fact]
    public void EffectiveArea_VisibleTaskbar_NoFullscreen_UsesWorkArea()
    {
        var layout = Layout(autoHide: false, fullScreen: false);
        Assert.Equal(layout.WorkArea, layout.EffectiveArea("bottom"));
    }

    [Fact]
    public void EffectiveArea_AutoHide_UsesBounds()
    {
        var layout = Layout(autoHide: true, fullScreen: false);
        Assert.Equal(layout.Bounds, layout.EffectiveArea("bottom"));
    }

    [Fact]
    public void EffectiveArea_Fullscreen_ConflictingEdge_UsesBounds()
    {
        var layout = Layout(autoHide: false, fullScreen: true, taskbarEdge: "bottom");
        Assert.Equal(layout.Bounds, layout.EffectiveArea("bottom"));
    }

    [Fact]
    public void EffectiveArea_Fullscreen_NonConflictingEdge_UsesWorkArea()
    {
        var layout = Layout(autoHide: false, fullScreen: true, taskbarEdge: "bottom");
        Assert.Equal(layout.WorkArea, layout.EffectiveArea("top"));
    }

    [Fact]
    public void ShouldRenderAboveTaskbar_OnlyWhenOverlappingTaskbar()
    {
        var layout = Layout(autoHide: false, fullScreen: true, taskbarEdge: "bottom");
        Assert.True(layout.ShouldRenderAboveTaskbar("bottom"));
        Assert.False(layout.ShouldRenderAboveTaskbar("top"));
    }
}
