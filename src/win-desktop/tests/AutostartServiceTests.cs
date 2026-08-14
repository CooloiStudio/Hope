using Hope.Desktop.Services;
using Xunit;

namespace Hope.Desktop.Tests;

public class AutostartServiceTests
{
    [Fact]
    public void StartupTaskId_MatchesAppxManifest()
    {
        var manifest = FindRepoFile(System.IO.Path.Combine("packaging", "AppxManifest.template.xml"));
        Assert.NotNull(manifest);
        var xml = System.IO.File.ReadAllText(manifest!);
        Assert.Contains("windows.startupTask", xml, StringComparison.Ordinal);
        Assert.Contains($"TaskId=\"{AutostartService.StartupTaskId}\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void RunValueName_IsHope() =>
        Assert.Equal("Hope", AutostartService.RunValueName);

    private static string? FindRepoFile(string relative)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, relative);
            if (System.IO.File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
