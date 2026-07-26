using Hope.Desktop.Services;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class CountdownEditModesTests
{
    [Theory]
    [InlineData(null, CountdownEditModes.Duration)]
    [InlineData("", CountdownEditModes.Duration)]
    [InlineData("duration", CountdownEditModes.Duration)]
    [InlineData("Duration", CountdownEditModes.Duration)]
    [InlineData("deadline", CountdownEditModes.Deadline)]
    [InlineData("DEADLINE", CountdownEditModes.Deadline)]
    [InlineData("bogus", CountdownEditModes.Duration)]
    public void Normalize_DefaultsToDuration(string? input, string expected)
    {
        Assert.Equal(expected, CountdownEditModes.Normalize(input));
    }

    [Fact]
    public void ForStorage_OmitsDefaultAndScheduled()
    {
        Assert.Null(CountdownEditModes.ForStorage(null, isInstant: true));
        Assert.Null(CountdownEditModes.ForStorage("duration", isInstant: true));
        Assert.Null(CountdownEditModes.ForStorage("deadline", isInstant: false));
        Assert.Equal(CountdownEditModes.Deadline, CountdownEditModes.ForStorage("deadline", isInstant: true));
    }
}
