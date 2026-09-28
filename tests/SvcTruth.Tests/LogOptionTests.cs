using System.Text.Json;
using Xunit;

namespace SvcTruth.Tests;

/// <summary>
/// --log &lt;file&gt;: the tail of a named log file is shown for every selected job (a service does not
/// have to write its errors where launchd expects them). A missing or unreadable file is reported
/// in the log field, never an error that stops the run.
/// </summary>
public class LogOptionTests
{
    private const string Home = "/Users/example";
    private const string OwnLogPath = "/Users/example/Library/Logs/bridge/bridge.log";
    private const string OwnLogTail = "2026-09-28T11:59:51Z bridge: fatal: connection refused (127.0.0.1:8081)";

    private static CommandResult Ok(string stdout) => new(0, stdout, string.Empty, TimedOut: false, FailedToStart: false);

    /// <summary>Two healthy jobs: bridge ran twice and exited cleanly, cache is running.</summary>
    private static CommandResult World(string fileName, IReadOnlyList<string> arguments, int call)
    {
        if (fileName == "launchctl" && arguments is ["list"])
        {
            return Ok(string.Join('\n',
                "PID\tStatus\tLabel",
                $"-\t0\t{SampleData.BridgeLabel}",
                $"501\t0\t{SampleData.CacheLabel}"));
        }

        if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.BridgeLabel}"])
        {
            return Ok(SampleData.PrintCrashLooping(SampleData.BridgeLabel, runs: 2, exitCode: 0, plistPath: SampleData.BridgePlistPath));
        }

        if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.CacheLabel}"])
        {
            return Ok(SampleData.PrintNeverExitedRunning(SampleData.CacheLabel, pid: 501, plistPath: "/Users/example/Library/LaunchAgents/cache.plist"));
        }

        return new CommandResult(127, string.Empty, "unexpected command", false, false);
    }

    private static (int Exit, string Stdout, string Stderr) Run(
        string[] args,
        Dictionary<string, string>? files = null,
        FakeFileSystem? fileSystem = null,
        string? home = null)
    {
        var fs = fileSystem ?? new FakeFileSystem(files ?? []);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = SvcTruthApp.Run(args, 501, new FakeCommandRunner { Handler = World }, fs,
            new FakeClock(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)), stdout, stderr, home).GetAwaiter().GetResult();
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void LogTailAppearsInDetailHumanOutput()
    {
        var files = new Dictionary<string, string> { [OwnLogPath] = $"{OwnLogTail}\n" };
        var (exit, stdout, stderr) = Run([SampleData.BridgeLabel, "--log", OwnLogPath], files);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("svc-truth:", stderr);
        Assert.Contains($"log: {OwnLogPath}", stdout);
        Assert.Contains(OwnLogTail, stdout);
    }

    [Fact]
    public void LogTailInJsonExpandsLeadingTilde()
    {
        var files = new Dictionary<string, string> { [OwnLogPath] = $"{OwnLogTail}\n" };
        var (exit, stdout, _) = Run([SampleData.BridgeLabel, "--log", "~/Library/Logs/bridge/bridge.log", "--json"],
            files, home: Home);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(stdout);
        var log = document.RootElement.GetProperty("jobs")[0].GetProperty("log");
        Assert.Equal(OwnLogPath, log.GetProperty("path").GetString()); // ~ expanded to the home directory
        Assert.Null(log.GetProperty("error").GetString());
        var tail = log.GetProperty("tail");
        Assert.Equal(OwnLogTail, tail[tail.GetArrayLength() - 1].GetString());
    }

    [Fact]
    public void MissingLogFileIsReportedNotFatal()
    {
        var (exit, stdout, stderr) = Run([SampleData.BridgeLabel, "--log", "/no/such/bridge.log"]);

        Assert.Equal(0, exit); // the job itself is healthy; a missing log never fails the run
        Assert.DoesNotContain("svc-truth:", stderr);
        Assert.Contains("log: /no/such/bridge.log", stdout);
        Assert.Contains("file not found", stdout);
    }

    [Fact]
    public void MissingLogFileIsReportedInTheJsonField()
    {
        var (exit, stdout, _) = Run([SampleData.BridgeLabel, "--log", "/no/such/bridge.log", "--json"]);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(stdout);
        var log = document.RootElement.GetProperty("jobs")[0].GetProperty("log");
        Assert.Equal("/no/such/bridge.log", log.GetProperty("path").GetString());
        Assert.Equal("file not found", log.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, log.GetProperty("tail").ValueKind);
    }

    [Fact]
    public void UnreadableLogFileIsReportedInTheJsonField()
    {
        var fileSystem = new FakeFileSystem(new Dictionary<string, string> { [OwnLogPath] = $"{OwnLogTail}\n" });
        fileSystem.UnreadableTailPaths.Add(OwnLogPath); // exists, but the tail cannot be read
        var (exit, stdout, _) = Run([SampleData.BridgeLabel, "--log", OwnLogPath, "--json"], fileSystem: fileSystem);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(stdout);
        var log = document.RootElement.GetProperty("jobs")[0].GetProperty("log");
        Assert.Equal("file not readable", log.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, log.GetProperty("tail").ValueKind);
    }

    [Fact]
    public void EmptyLogFileYieldsNullTailWithoutError()
    {
        var files = new Dictionary<string, string> { [OwnLogPath] = string.Empty };
        var (exit, stdout, _) = Run([SampleData.BridgeLabel, "--log", OwnLogPath, "--json"], files);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(stdout);
        var log = document.RootElement.GetProperty("jobs")[0].GetProperty("log");
        Assert.Null(log.GetProperty("error").GetString()); // read fine, just no lines
        Assert.Equal(JsonValueKind.Null, log.GetProperty("tail").ValueKind);
    }

    [Fact]
    public void LogTailAppearsUnderEachJobInAListing()
    {
        var files = new Dictionary<string, string> { [OwnLogPath] = $"{OwnLogTail}\n" };
        var (exit, stdout, _) = Run(["io.github.example", "--log", OwnLogPath], files);

        Assert.Equal(0, exit);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Count(l => l == $"  {OwnLogTail}")); // once under each of the two jobs
        Assert.Contains(lines, l => l.StartsWith(SampleData.BridgeLabel));
        Assert.Contains(lines, l => l.StartsWith(SampleData.CacheLabel));
    }

    [Fact]
    public void LogNeedsAPath()
    {
        var (exit, _, stderr) = Run([SampleData.BridgeLabel, "--log"]);

        Assert.Equal(SvcTruthApp.ExitUsageOrReadError, exit);
        Assert.Contains("--log needs a file path", stderr);
    }
}
