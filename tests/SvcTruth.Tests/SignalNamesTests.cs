using SvcTruth.Launchd;
using Xunit;

namespace SvcTruth.Tests;

public class SignalNamesTests
{
    [Theory]
    [InlineData(1, "SIGHUP")]
    [InlineData(2, "SIGINT")]
    [InlineData(9, "SIGKILL")]
    [InlineData(11, "SIGSEGV")]
    [InlineData(15, "SIGTERM")]
    [InlineData(30, "SIGUSR1")]
    [InlineData(31, "SIGUSR2")]
    public void MapsKnownSignals(int number, string expected)
    {
        Assert.Equal(expected, SignalNames.NameFor(number));
    }

    [Fact]
    public void UnknownSignalNumberHasNoName()
    {
        Assert.Null(SignalNames.NameFor(99));
    }

    [Fact]
    public void DescribesExitCodeAndSignals()
    {
        Assert.Equal("exit code 78", SignalNames.Describe(78));
        Assert.Equal("killed by SIGTERM (signal 15)", SignalNames.Describe(-15));
        Assert.Equal("killed by signal 99", SignalNames.Describe(-99));
    }
}
