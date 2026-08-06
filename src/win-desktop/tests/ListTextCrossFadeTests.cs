using Hope.Desktop.Controls;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class ListTextCrossFadeTests
{
    [Fact]
    public void SetContentEnabled_RaisesOnlyOnChange()
    {
        ListTextCrossFade.SetContentEnabled(false);
        var count = 0;
        void Handler() => count++;
        ListTextCrossFade.ContentEnabledChanged += Handler;
        try
        {
            ListTextCrossFade.SetContentEnabled(false);
            Assert.Equal(0, count);
            Assert.False(ListTextCrossFade.ContentEnabled);

            ListTextCrossFade.SetContentEnabled(true);
            Assert.Equal(1, count);
            Assert.True(ListTextCrossFade.ContentEnabled);

            ListTextCrossFade.SetContentEnabled(true);
            Assert.Equal(1, count);

            ListTextCrossFade.SetContentEnabled(false);
            Assert.Equal(2, count);
            Assert.False(ListTextCrossFade.ContentEnabled);
        }
        finally
        {
            ListTextCrossFade.ContentEnabledChanged -= Handler;
            ListTextCrossFade.SetContentEnabled(false);
        }
    }

    [Fact]
    public void CycleDuration_MatchesHoldAndFade()
    {
        var expected = TimeSpan.FromSeconds(
            ListTextCrossFade.HoldSeconds * 2 + ListTextCrossFade.FadeSeconds * 2);
        Assert.Equal(expected, ListTextCrossFade.CycleDuration);
    }
}
