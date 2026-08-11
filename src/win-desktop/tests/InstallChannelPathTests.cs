using Hope.Desktop.Services;
using Xunit;

namespace Hope.Desktop.Tests;

public class InstallChannelPathTests
{
    [Fact]
    public void BuildVirtualizedRoamingRoot_MatchesMsixLocalCacheLayout()
    {
        var root = InstallChannel.BuildVirtualizedRoamingRoot(
            @"C:\Users\demo\AppData\Local",
            "Cooloi.Hope_c6tv1djd4qth2");

        Assert.Equal(
            @"C:\Users\demo\AppData\Local\Packages\Cooloi.Hope_c6tv1djd4qth2\LocalCache\Roaming",
            root);
    }
}
