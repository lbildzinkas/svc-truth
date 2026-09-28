using SvcTruth.Launchd;
using Xunit;

namespace SvcTruth.Tests;

public class LaunchctlListParserTests
{
    [Fact]
    public void ParsesRowsWithPidStatusAndLabel()
    {
        var entries = LaunchctlListParser.Parse(SampleData.ListOutput);

        Assert.Equal(3, entries.Count);
        var bridge = Assert.Single(entries, e => e.Label == SampleData.BridgeLabel);
        Assert.Null(bridge.Pid);
        Assert.Equal(78, bridge.LastExitStatus);

        var cache = Assert.Single(entries, e => e.Label == SampleData.CacheLabel);
        Assert.Equal(501, cache.Pid);
        Assert.Equal(0, cache.LastExitStatus);

        var ghost = Assert.Single(entries, e => e.Label == SampleData.GhostLabel);
        Assert.Null(ghost.Pid);
        Assert.Equal(0, ghost.LastExitStatus);
    }

    [Fact]
    public void ParsesDashPidAndDashStatus()
    {
        var entries = LaunchctlListParser.Parse("PID\tStatus\tLabel\n-\t-\tcom.example.none");

        var entry = Assert.Single(entries);
        Assert.Equal("com.example.none", entry.Label);
        Assert.Null(entry.Pid);
        Assert.Null(entry.LastExitStatus);
    }

    [Fact]
    public void ParsesNegativeStatusAsSignal()
    {
        var entries = LaunchctlListParser.Parse($"PID\tStatus\tLabel\n-\t-15\t{SampleData.BridgeLabel}");

        var entry = Assert.Single(entries);
        Assert.Equal(-15, entry.LastExitStatus);
    }

    [Fact]
    public void SkipsHeaderAndBlankLines()
    {
        var entries = LaunchctlListParser.Parse("\nPID\tStatus\tLabel\n\n");

        Assert.Empty(entries);
    }
}
