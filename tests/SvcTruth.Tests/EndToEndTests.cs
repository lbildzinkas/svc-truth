using System.Text.Json;
using Xunit;

namespace SvcTruth.Tests;

public class EndToEndTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static CommandResult Ok(string stdout) => new(0, stdout, string.Empty, TimedOut: false, FailedToStart: false);

    /// <summary>Healthy world: bridge ran twice and exited cleanly, cache runs clean, ghost never ran.</summary>
    private static CommandResult HealthyWorld(string fileName, string arguments, int call)
    {
        if (fileName == "launchctl" && arguments == "list")
        {
            return Ok(SampleData.ListOutput);
        }

        if (fileName == "launchctl" && arguments == $"print gui/501/{SampleData.BridgeLabel}")
        {
            return Ok(SampleData.PrintCrashLooping(SampleData.BridgeLabel, runs: 2, exitCode: 0, plistPath: SampleData.BridgePlistPath));
        }

        if (fileName == "launchctl" && arguments == $"print gui/501/{SampleData.CacheLabel}")
        {
            return Ok(SampleData.PrintNeverExitedRunning(SampleData.CacheLabel, pid: 501, plistPath: "/Users/example/Library/LaunchAgents/cache.plist"));
        }

        if (fileName == "launchctl" && arguments == $"print gui/501/{SampleData.GhostLabel}")
        {
            return Ok(SampleData.PrintNeverRan(SampleData.GhostLabel));
        }

        if (fileName == "/bin/sh")
        {
            return Ok("all checks passed\n");
        }

        return new CommandResult(127, string.Empty, "unexpected command", false, false);
    }

    private static CommandResult ExitedFailedWorld(string fileName, string arguments, int call)
    {
        if (fileName == "launchctl" && arguments == $"print gui/501/{SampleData.BridgeLabel}")
        {
            // runs 2 with exit 78 and no rise across the resample: a one-time failure, not a loop.
            return Ok(SampleData.PrintCrashLooping(SampleData.BridgeLabel, runs: 2, exitCode: 78, plistPath: SampleData.BridgePlistPath));
        }

        return HealthyWorld(fileName, arguments, call);
    }

    private static (int Exit, string Stdout, string Stderr, FakeCommandRunner Runner, FakeClock Clock) RunApp(
        string[] args,
        Func<string, string, int, CommandResult> handler,
        Dictionary<string, string>? files = null)
    {
        var runner = new FakeCommandRunner { Handler = handler };
        var fileSystem = new FakeFileSystem(files ?? new Dictionary<string, string>());
        var clock = new FakeClock(Start);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = SvcTruthApp.Run(args, 501, runner, fileSystem, clock, stdout, stderr).GetAwaiter().GetResult();
        return (exit, stdout.ToString(), stderr.ToString(), runner, clock);
    }

    [Fact]
    public void HealthyListingExitsZeroAndPrintsShortLines()
    {
        var (exit, stdout, stderr, _, clock) = RunApp([], HealthyWorld);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("svc-truth:", stderr);
        foreach (var label in new[] { SampleData.BridgeLabel, SampleData.CacheLabel, SampleData.GhostLabel })
        {
            Assert.Contains(label, stdout);
        }

        Assert.Contains("HEALTHY", stdout);
        Assert.Contains("LOADED-NEVER-RAN", stdout);
        Assert.Empty(clock.Slept); // nothing suspicious: no sampling delay
    }

    [Fact]
    public void PrefixFiltersTheListing()
    {
        var (exit, stdout, _, _, _) = RunApp(["io.github.example.cache"], HealthyWorld);

        Assert.Equal(0, exit);
        Assert.Contains(SampleData.CacheLabel, stdout);
        Assert.DoesNotContain(SampleData.BridgeLabel, stdout);
    }

    [Fact]
    public void NoMatchIsAReadError()
    {
        var (exit, _, stderr, _, _) = RunApp(["no.such.prefix"], HealthyWorld);

        Assert.Equal(SvcTruthApp.ExitUsageOrReadError, exit);
        Assert.Contains("no jobs match", stderr);
    }

    [Fact]
    public void ExactLabelPrintsDetailWithLogPathsAndTail()
    {
        var files = new Dictionary<string, string>
        {
            [SampleData.BridgePlistPath] = SampleData.BridgePlist,
            [SampleData.BridgeStderrPath] = SampleData.BridgeStderrTail,
        };
        var (exit, stdout, _, _, _) = RunApp([SampleData.BridgeLabel], ExitedFailedWorld, files);

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit); // exited-failed
        Assert.Contains(SampleData.BridgeStderrPath, stdout);
        Assert.Contains("connection refused", stdout);
        Assert.Contains("exit code 78", stdout);
    }

    [Fact]
    public void StdoutTailIsUsedWhenStderrLogIsEmpty()
    {
        var files = new Dictionary<string, string>
        {
            [SampleData.BridgePlistPath] = SampleData.BridgePlist,
            [SampleData.BridgeStderrPath] = string.Empty,
            [SampleData.BridgeStdoutPath] = "started\nlistening on 8081\n",
        };
        var (exit, stdout, _, _, _) = RunApp([SampleData.BridgeLabel, "--json"], ExitedFailedWorld, files);

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit);
        using var document = JsonDocument.Parse(stdout);
        var job = document.RootElement.GetProperty("jobs")[0];
        Assert.False(job.TryGetProperty("stderrTail", out _)); // empty stderr log: omitted
        var stdoutTail = job.GetProperty("stdoutTail");
        Assert.Equal("listening on 8081", stdoutTail[stdoutTail.GetArrayLength() - 1].GetString());
    }

    [Fact]
    public void CrashLoopSamplingWithDoctorContradictionExitsTwo()
    {
        CommandResult CrashWorld(string fileName, string arguments, int call)
        {
            if (fileName == "launchctl" && arguments == $"print gui/501/{SampleData.BridgeLabel}")
            {
                var runs = call == 1 ? 120 : 124; // second sample taken after the sleep
                return Ok(SampleData.PrintCrashLooping(SampleData.BridgeLabel, runs, exitCode: 78, plistPath: SampleData.BridgePlistPath));
            }

            return HealthyWorld(fileName, arguments, call);
        }

        var files = new Dictionary<string, string>
        {
            [SampleData.BridgePlistPath] = SampleData.BridgePlist,
            [SampleData.BridgeStderrPath] = SampleData.BridgeStderrTail,
        };
        var (exit, stdout, _, runner, clock) = RunApp(
            [SampleData.BridgeLabel, "--doctor", "bridge doctor", "--json"], CrashWorld, files);

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit);
        var sleep = Assert.Single(clock.Slept);
        Assert.Equal(TimeSpan.FromSeconds(3), sleep);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("gui/501", root.GetProperty("domain").GetString());
        var job = root.GetProperty("jobs")[0];
        Assert.Equal(SampleData.BridgeLabel, job.GetProperty("label").GetString());
        Assert.Equal("crash-looping", job.GetProperty("verdict").GetString());
        Assert.Equal(120, job.GetProperty("runCount").GetInt32()); // the first sample's run count
        Assert.Equal(78, job.GetProperty("lastExitCode").GetInt32());
        Assert.True(job.GetProperty("restartRatePerMinute").GetDouble() > 0);
        Assert.True(job.GetProperty("restartIntervalSeconds").GetDouble() > 0);
        var stderrTail = job.GetProperty("stderrTail");
        Assert.Equal("2026-09-28T11:59:51Z bridge: fatal: connection refused (127.0.0.1:8081)",
            stderrTail[stderrTail.GetArrayLength() - 1].GetString());
        var doctor = job.GetProperty("doctor");
        Assert.Equal("bridge doctor", doctor.GetProperty("command").GetString());
        Assert.Equal(0, doctor.GetProperty("exitCode").GetInt32());
        Assert.True(doctor.GetProperty("contradiction").GetBoolean());
        var summary = root.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("crashLooping").GetInt32());
        Assert.Equal(1, summary.GetProperty("contradictions").GetInt32());
        Assert.Equal(2, root.GetProperty("exitCode").GetInt32());

        // The doctor command was run through sh exactly once for the one selected job.
        Assert.Contains(runner.Invocations, i => i.FileName == "/bin/sh" && i.Arguments == "-c 'bridge doctor'");
    }

    [Fact]
    public void DoctorExitZeroWithHealthyVerdictIsNoContradiction()
    {
        var files = new Dictionary<string, string>
        {
            ["/Users/example/Library/LaunchAgents/cache.plist"] = SampleData.BridgePlist,
        };
        var (exit, stdout, _, _, _) = RunApp([SampleData.CacheLabel, "--doctor", "cache doctor", "--json"], HealthyWorld, files);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(stdout);
        var doctor = document.RootElement.GetProperty("jobs")[0].GetProperty("doctor");
        Assert.Equal(0, doctor.GetProperty("exitCode").GetInt32());
        Assert.False(doctor.GetProperty("contradiction").GetBoolean());
    }

    [Fact]
    public void DoctorTimeoutIsReportedWithoutContradiction()
    {
        Func<string, string, int, CommandResult> TimeoutDoctor(Func<string, string, int, CommandResult> world) =>
            (fileName, arguments, call) =>
                fileName == "/bin/sh"
                    ? new CommandResult(null, string.Empty, string.Empty, TimedOut: true, FailedToStart: false)
                    : world(fileName, arguments, call);

        var (exit, stdout, _, _, _) = RunApp([SampleData.BridgeLabel, "--doctor", "slow doctor", "--json"], TimeoutDoctor(ExitedFailedWorld));

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit); // exited-failed still drives the exit code
        using var document = JsonDocument.Parse(stdout);
        var doctor = document.RootElement.GetProperty("jobs")[0].GetProperty("doctor");
        Assert.True(doctor.GetProperty("timedOut").GetBoolean());
        Assert.False(doctor.TryGetProperty("exitCode", out _));
        Assert.False(doctor.GetProperty("contradiction").GetBoolean());
    }

    [Fact]
    public void UnreadablePrintYieldsUnknownAndExitThree()
    {
        CommandResult BrokenWorld(string fileName, string arguments, int call)
        {
            if (fileName == "launchctl" && arguments == "list")
            {
                return Ok(SampleData.ListOutputNoStatus); // not even the list row has a status
            }

            if (fileName == "launchctl" && arguments.StartsWith("print "))
            {
                return new CommandResult(1, SampleData.PrintNotFound("x"), "Bad request.", false, false);
            }

            return HealthyWorld(fileName, arguments, call);
        }

        var (exit, stdout, _, _, _) = RunApp([SampleData.BridgeLabel, "--json"], BrokenWorld);

        Assert.Equal(SvcTruthApp.ExitUsageOrReadError, exit);
        using var document = JsonDocument.Parse(stdout);
        var job = document.RootElement.GetProperty("jobs")[0];
        Assert.Equal("unknown", job.GetProperty("verdict").GetString());
        Assert.False(job.TryGetProperty("state", out _));
        Assert.False(job.TryGetProperty("runCount", out _));
    }

    [Fact]
    public void UnprintableJobFallsBackToListData()
    {
        // Some system-provided jobs cannot be printed; the listing row still says whether they run.
        CommandResult UnprintableWorld(string fileName, string arguments, int call)
        {
            if (fileName == "launchctl" && arguments == "list")
            {
                return Ok(SampleData.ListOutputBridgeRunning);
            }

            if (fileName == "launchctl" && arguments.StartsWith("print "))
            {
                return new CommandResult(1, SampleData.PrintNotFound("x"), "Bad request.", false, false);
            }

            return HealthyWorld(fileName, arguments, call);
        }

        var (exit, stdout, _, runner, clock) = RunApp([SampleData.BridgeLabel, "--json"], UnprintableWorld);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(stdout);
        var job = document.RootElement.GetProperty("jobs")[0];
        Assert.Equal("healthy", job.GetProperty("verdict").GetString());
        Assert.Equal("running", job.GetProperty("state").GetString());
        Assert.Equal(4242, job.GetProperty("pid").GetInt32());
        Assert.False(job.TryGetProperty("runCount", out _)); // print unreadable: run count stays unknown
        Assert.Contains("launchctl print failed", job.GetProperty("reason").GetString());
        Assert.Empty(clock.Slept); // last exit 0 from the list: nothing suspicious, no sampling
    }

    [Fact]
    public void UnprintableJobWithFailedExitIsExitedFailedFromListData()
    {
        CommandResult UnprintableWorld(string fileName, string arguments, int call)
        {
            if (fileName == "launchctl" && arguments.StartsWith("print "))
            {
                return new CommandResult(1, SampleData.PrintNotFound("x"), "Bad request.", false, false);
            }

            return HealthyWorld(fileName, arguments, call); // list shows bridge: no pid, status 78
        }

        var (exit, stdout, _, _, _) = RunApp([SampleData.BridgeLabel, "--json"], UnprintableWorld);

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit);
        using var document = JsonDocument.Parse(stdout);
        var job = document.RootElement.GetProperty("jobs")[0];
        Assert.Equal("exited-failed", job.GetProperty("verdict").GetString());
        Assert.Equal(78, job.GetProperty("lastExitCode").GetInt32());
    }

    [Fact]
    public void LaunchctlListFailureIsAReadError()
    {
        CommandResult NoList(string fileName, string arguments, int call) =>
            fileName == "launchctl" && arguments == "list"
                ? new CommandResult(1, string.Empty, "Could not communicate with launchd", false, false)
                : HealthyWorld(fileName, arguments, call);

        var (exit, _, stderr, _, _) = RunApp([], NoList);

        Assert.Equal(SvcTruthApp.ExitUsageOrReadError, exit);
        Assert.Contains("could not read the gui domain", stderr);
    }

    [Fact]
    public void UsageErrorsPrintUsageAndExitThree()
    {
        var (exit, _, stderr, _, _) = RunApp(["--bogus"], HealthyWorld);
        Assert.Equal(SvcTruthApp.ExitUsageOrReadError, exit);
        Assert.Contains("unknown option", stderr);

        var (exit2, _, stderr2, _, _) = RunApp(["--doctor"], HealthyWorld);
        Assert.Equal(SvcTruthApp.ExitUsageOrReadError, exit2);
        Assert.Contains("--doctor needs a command", stderr2);
    }

    [Fact]
    public void HelpAndVersionSucceed()
    {
        var (exit, stdout, _, _, _) = RunApp(["--help"], HealthyWorld);
        Assert.Equal(0, exit);
        Assert.Contains("svc-truth [label]", stdout);

        var (exit2, stdout2, _, _, _) = RunApp(["--version"], HealthyWorld);
        Assert.Equal(0, exit2);
        Assert.StartsWith("svc-truth ", stdout2);
    }
}
