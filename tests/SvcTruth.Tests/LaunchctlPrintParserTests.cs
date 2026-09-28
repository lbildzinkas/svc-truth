using SvcTruth.Launchd;
using Xunit;

namespace SvcTruth.Tests;

public class LaunchctlPrintParserTests
{
    [Fact]
    public void ParsesTopLevelFieldsAndIgnoresNestedBlocks()
    {
        var info = LaunchctlPrintParser.Parse(
            SampleData.PrintRunning(SampleData.BridgeLabel, pid: 4242, runs: 7, plistPath: SampleData.BridgePlistPath));

        Assert.NotNull(info);
        Assert.Equal("running", info!.State);
        Assert.Equal(4242, info.Pid);
        Assert.Equal(7, info.Runs);
        Assert.Equal(0, info.LastExitCode);
        Assert.False(info.NeverExited);
        Assert.Equal(SampleData.BridgePlistPath, info.PlistPath);
    }

    [Fact]
    public void ParsesNeverExited()
    {
        var info = LaunchctlPrintParser.Parse(SampleData.PrintNeverRan(SampleData.GhostLabel));

        Assert.NotNull(info);
        Assert.True(info!.NeverExited);
        Assert.Null(info.LastExitCode);
        Assert.Equal(0, info.Runs);
        Assert.Null(info.Pid);
    }

    [Fact]
    public void ParsesNegativeExitAsSignal()
    {
        var info = LaunchctlPrintParser.Parse(
            SampleData.PrintKilledBySignal(SampleData.BridgeLabel, runs: 2, signal: 15));

        Assert.NotNull(info);
        Assert.Equal(-15, info!.LastExitCode);
        Assert.Equal(2, info.Runs);
    }

    [Fact]
    public void ParsesNeverExitedWhileRunning()
    {
        var info = LaunchctlPrintParser.Parse(
            SampleData.PrintNeverExitedRunning(SampleData.CacheLabel, pid: 501, plistPath: "/Users/example/Library/LaunchAgents/cache.plist"));

        Assert.NotNull(info);
        Assert.Equal("running", info!.State);
        Assert.True(info.NeverExited);
        Assert.Null(info.LastExitCode);
    }

    [Fact]
    public void ParsesDecoratedExitCode()
    {
        // launchd decorates some statuses: "78: EX_CONFIG".
        var output = string.Join('\n',
            "gui/501/com.example.job = {",
            "\tstate = spawn scheduled",
            "\truns = 1",
            "\tlast exit code = 78: EX_CONFIG",
            "}");

        var info = LaunchctlPrintParser.Parse(output);

        Assert.NotNull(info);
        Assert.Equal(78, info!.LastExitCode);
        Assert.False(info.NeverExited);
        Assert.Equal("spawn scheduled", info.State);
    }

    [Fact]
    public void ReturnsNullForErrorOutput()
    {
        Assert.Null(LaunchctlPrintParser.Parse(SampleData.PrintNotFound("no.such.job")));
    }

    [Fact]
    public void ParenthesisedPathOfAppSubmittedJobsBecomesNull()
    {
        var output = string.Join('\n',
            "gui/501/application.pro.app.123 = {",
            "\tpath = (submitted by runningboardd.413)",
            "\tstate = running",
            "\truns = 1",
            "\tlast exit code = (never exited)",
            "}");

        var info = LaunchctlPrintParser.Parse(output);

        Assert.NotNull(info);
        Assert.Null(info!.PlistPath);
    }
}
