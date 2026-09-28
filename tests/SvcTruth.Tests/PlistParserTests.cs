using SvcTruth.Launchd;
using Xunit;

namespace SvcTruth.Tests;

public class PlistParserTests
{
    [Fact]
    public void ReadsStandardLogPaths()
    {
        var (stdout, stderr) = PlistParser.GetLogPaths(SampleData.BridgePlist);

        Assert.Equal(SampleData.BridgeStdoutPath, stdout);
        Assert.Equal(SampleData.BridgeStderrPath, stderr);
    }

    [Fact]
    public void ReturnsNullsWhenKeysAreAbsent()
    {
        var plist = string.Join('\n',
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<plist version=\"1.0\"><dict><key>Label</key><string>x</string></dict></plist>");

        var (stdout, stderr) = PlistParser.GetLogPaths(plist);

        Assert.Null(stdout);
        Assert.Null(stderr);
    }

    [Fact]
    public void ReturnsNullsForBinaryPlists()
    {
        var (stdout, stderr) = PlistParser.GetLogPaths("bplist00\xd0\x0f");

        Assert.Null(stdout);
        Assert.Null(stderr);
    }

    [Fact]
    public void ReturnsNullsForMalformedXml()
    {
        var (stdout, stderr) = PlistParser.GetLogPaths("<plist><dict>");

        Assert.Null(stdout);
        Assert.Null(stderr);
    }

    [Fact]
    public void ReturnsNullsForNullInput()
    {
        var (stdout, stderr) = PlistParser.GetLogPaths(null);

        Assert.Null(stdout);
        Assert.Null(stderr);
    }
}
