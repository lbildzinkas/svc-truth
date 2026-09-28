using Xunit;

namespace SvcTruth.Tests;

public class EtimeParserTests
{
    [Theory]
    [InlineData("45", 0, 0, 0, 45)]
    [InlineData("05:33", 0, 0, 5, 33)]
    [InlineData("02:03:44", 0, 2, 3, 44)]
    [InlineData("1-05:10:22", 1, 5, 10, 22)]
    [InlineData("  07:12  ", 0, 0, 7, 12)] // ps pads the etime column with spaces
    public void ParsesEtimeFormats(string output, int days, int hours, int minutes, int seconds)
    {
        var parsed = EtimeParser.Parse(output);

        Assert.NotNull(parsed);
        Assert.Equal(new TimeSpan(days, hours, minutes, seconds), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-time")]
    [InlineData("1-02:03:04:05")]
    public void UnparseableEtimeYieldsNull(string output)
    {
        Assert.Null(EtimeParser.Parse(output));
    }
}
