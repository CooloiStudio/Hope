using Hope.Desktop.Ipc;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class IpcMessageDispatcherTests
{
    [Fact]
    public void Dispatch_TasksType_InvokesTasksCallback()
    {
        List<TaskDto>? got = null;
        var ok = IpcMessageDispatcher.TryDispatch(
            """{"type":"tasks","tasks":[{"id":"a","name":"n","endTs":1}]}""",
            onTasks: t => got = t);

        Assert.True(ok);
        Assert.NotNull(got);
        Assert.Single(got!);
        Assert.Equal("a", got![0].Id);
    }

    [Fact]
    public void Dispatch_SettingsType_InvokesSettingsCallback()
    {
        SettingsDto? got = null;
        var ok = IpcMessageDispatcher.TryDispatch(
            """{"type":"settings","settings":{"barHeightPx":7,"barPosition":"bottom"}}""",
            onSettings: s => got = s);

        Assert.True(ok);
        Assert.NotNull(got);
        Assert.Equal(7, got!.BarHeightPx);
        Assert.Equal("bottom", got.BarPosition);
    }

    [Fact]
    public void Dispatch_VersionType_InvokesVersionCallback()
    {
        string? ver = null;
        Assert.True(IpcMessageDispatcher.TryDispatch(
            """{"type":"version","version":"0.13.91"}""",
            onVersion: v => ver = v));
        Assert.Equal("0.13.91", ver);
    }

    [Fact]
    public void Dispatch_StateBroadcast_InvokesStateCallback()
    {
        StateMessage? msg = null;
        Assert.True(IpcMessageDispatcher.TryDispatch(
            """{"version":1,"visible":true,"state":"running","segments":[]}""",
            onState: s => msg = s));
        Assert.NotNull(msg);
        Assert.True(msg!.Visible);
        Assert.Equal("running", msg.State);
    }

    [Fact]
    public void Dispatch_BadJson_ReturnsFalseWithoutThrowing()
    {
        var called = false;
        var ok = IpcMessageDispatcher.TryDispatch(
            "{not-json",
            onState: _ => called = true,
            onTasks: _ => called = true,
            onSettings: _ => called = true);
        Assert.False(ok);
        Assert.False(called);
    }

    [Fact]
    public void Dispatch_BlankLine_ReturnsFalse()
    {
        Assert.False(IpcMessageDispatcher.TryDispatch("   "));
    }
}
