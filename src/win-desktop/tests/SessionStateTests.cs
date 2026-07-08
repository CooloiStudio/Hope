using Hope.Desktop.Ipc;
using Hope.Desktop.Overlay;
using Hope.Desktop.State;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class SessionStateTests
{
    [Fact]
    public void ApplySettings_IncrementsRevisionAndNotifies()
    {
        var session = new SessionState();
        int rev = 0;
        session.SettingsChanged += s =>
        {
            rev = session.SettingsRevision;
            Assert.Equal(4, s.BarHeightPx);
        };

        session.ApplySettings(new SettingsDto { BarHeightPx = 4 });

        Assert.True(session.SettingsHydrated);
        Assert.Equal(1, session.SettingsRevision);
        Assert.Equal(1, rev);
    }

    [Fact]
    public void ApplyTasks_IncrementsRevision()
    {
        var session = new SessionState();
        session.ApplyTasks(new List<TaskDto> { new() { Id = "a", Name = "t", EndTs = DateTimeOffset.Now.ToUnixTimeSeconds() } });

        Assert.True(session.TasksHydrated);
        Assert.Equal(1, session.TasksRevision);
        Assert.Single(session.Tasks);
    }

    [Fact]
    public void OverlayDefaults_BeforeHydration()
    {
        var session = new SessionState();

        Assert.Equal(4, session.OverlayBarHeightPx);
        Assert.Equal(OverlayWindow.PositionTop, session.OverlayBarPosition);
        Assert.False(session.OverlayAllFour);
        Assert.Equal("forward", session.DirectionForPosition("top"));
    }

    [Fact]
    public void DirectionForPosition_AllFourAlwaysForward()
    {
        var session = new SessionState();
        session.ApplySettings(new SettingsDto { AllFour = true, BarDirection = "reverse" });

        Assert.Equal("forward", session.DirectionForPosition("left"));
    }

    [Fact]
    public void WriteGuard_BlocksCommitDuringCompleteFlow()
    {
        var session = new SessionState();
        session.ApplySettings(new SettingsDto());

        Assert.True(session.Write.CanCommitSettings(session));

        session.Write.AddPendingComplete("t1");
        Assert.False(session.Write.CanAutoSaveTask(session, "t1"));
        Assert.True(session.Write.CanAutoSaveTask(session, "other"));

        session.Write.LoadingTask = true;
        Assert.False(session.Write.CanCommitSettings(session));
        Assert.False(session.Write.CanAutoSaveTask(session, "other"));
    }
}
