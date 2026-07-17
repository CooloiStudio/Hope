using System.IO;
using Hope.Desktop;
using Hope.Desktop.Services;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class StabilityHelperTests
{
    [Fact]
    public void OnceFlag_FiresOnlyOnce()
    {
        var once = new OnceFlag();
        var n = 0;
        Assert.True(once.TryFire(() => n++));
        Assert.False(once.TryFire(() => n++));
        Assert.Equal(1, n);
        Assert.True(once.HasFired);
    }

    [Fact]
    public void HeadlessSupervisor_ShouldSkipSpawn_WhenCoreReachable()
    {
        Assert.True(HeadlessSupervisor.ShouldSkipSpawn(coreReachable: true));
        Assert.False(HeadlessSupervisor.ShouldSkipSpawn(coreReachable: false));
    }

    [Fact]
    public void HeadlessSupervisor_ShouldSkipSpawn_WhenPaused()
    {
        Assert.True(HeadlessSupervisor.ShouldSkipSpawn(coreReachable: false, spawnPaused: true));
        Assert.True(HeadlessSupervisor.ShouldSkipSpawn(coreReachable: true, spawnPaused: true));
        Assert.False(HeadlessSupervisor.ShouldSkipSpawn(coreReachable: false, spawnPaused: false));
    }

    [Fact]
    public void FileSha256_MatchesKnownContent()
    {
        var path = Path.Combine(Path.GetTempPath(), "hope-sha-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            var hex = FileSha256.ComputeHex(path);
            Assert.Equal(64, hex.Length);
            Assert.True(FileSha256.Matches(path, hex));
            Assert.True(FileSha256.Matches(path, hex.ToLowerInvariant()));
            Assert.False(FileSha256.Matches(path, new string('0', 64)));
            Assert.False(FileSha256.Matches(path, null));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
