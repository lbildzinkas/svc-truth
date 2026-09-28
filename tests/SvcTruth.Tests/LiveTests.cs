using System.Text.Json;
using Xunit;

namespace SvcTruth.Tests;

/// <summary>
/// Opt-in tests that read the real gui domain, read-only. Off by default; enable with
/// SVC_TRUTH_LIVE_TESTS=1 (for example: dotnet test -e SVC_TRUTH_LIVE_TESTS=1).
/// </summary>
public class LiveTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("SVC_TRUTH_LIVE_TESTS") == "1";

    [Fact]
    public async Task LiveListingReadsTheRealGuiDomain()
    {
        if (!Enabled)
        {
            return; // opt-in: not enabled in this run
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SvcTruthApp.Run(
            ["--json"],
            Program.CurrentUid(),
            new ProcessCommandRunner(),
            new RealFileSystem(),
            new RealClock(),
            stdout,
            stderr);

        Assert.Contains(exit, new[] { SvcTruthApp.ExitHealthy, SvcTruthApp.ExitUnhealthy, SvcTruthApp.ExitUsageOrReadError });
        using var document = JsonDocument.Parse(stdout.ToString());
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var jobs = root.GetProperty("jobs");
        Assert.True(jobs.GetArrayLength() > 0, "expected the gui domain to contain jobs");
        foreach (var job in jobs.EnumerateArray())
        {
            var verdict = job.GetProperty("verdict").GetString();
            Assert.Contains(verdict, new[] { "healthy", "crash-looping", "loaded-never-ran", "exited-failed", "unknown" });
        }
    }

    [Fact]
    public async Task LiveExactLabelShowsOneJobInDetail()
    {
        if (!Enabled)
        {
            return; // opt-in: not enabled in this run
        }

        // Discover one real label first, then ask for it exactly.
        var listOutput = new ProcessCommandRunner().Run("launchctl", "list", TimeSpan.FromSeconds(10));
        var entries = SvcTruth.Launchd.LaunchctlListParser.Parse(listOutput.Stdout);
        Assert.NotEmpty(entries);
        var label = entries[0].Label;

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SvcTruthApp.Run(
            [label],
            Program.CurrentUid(),
            new ProcessCommandRunner(),
            new RealFileSystem(),
            new RealClock(),
            stdout,
            stderr);

        Assert.Contains(exit, new[] { SvcTruthApp.ExitHealthy, SvcTruthApp.ExitUnhealthy, SvcTruthApp.ExitUsageOrReadError });
        Assert.Contains(label, stdout.ToString());
    }
}
