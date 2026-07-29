using Hope.Desktop;
using Xunit;

namespace Hope.Desktop.Tests;

public sealed class TextDisplayWidthTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("下班", 4)]
    [InlineData("a下班b", 6)]
    [InlineData("１２", 4)] // 全角数字
    public void Measure_CountsHalfAndFullWidth(string s, int expected)
        => Assert.Equal(expected, TextDisplayWidth.Measure(s));

    [Fact]
    public void TruncateTaskName_KeepsAtMost16HanWidth()
    {
        var longName = new string('中', 20);
        var truncated = TextDisplayWidth.TruncateTaskName(longName);
        Assert.Equal(16, truncated.Length);
        Assert.Equal(32, TextDisplayWidth.Measure(truncated));
        Assert.False(TextDisplayWidth.ExceedsTaskNameLimit(truncated));
    }

    [Fact]
    public void TruncateTaskName_MixesHalfAndFull()
    {
        // 15 个汉字 = 30，再加 3 个半角应只留下 2 个半角
        var s = new string('汉', 15) + "abc";
        var truncated = TextDisplayWidth.TruncateTaskName(s);
        Assert.Equal(new string('汉', 15) + "ab", truncated);
        Assert.Equal(32, TextDisplayWidth.Measure(truncated));
    }
}
