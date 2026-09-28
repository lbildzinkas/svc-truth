using System.Text.Json;
using Xunit;

namespace SvcTruth.Tests;

public class EndToEndTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static CommandResult Ok(string stdout) => new(0, stdout, string.Empty, TimedOut: false, FailedToStart: false);

    /// <summary>Healthy world: bridge ran twice and exited cleanly, cache runs clean, ghost never ran.</summary>
    private static CommandResult HealthyWorld(string fileName, IReadOnlyList<string> arguments, int call)
    {
        if (fileName == "launchctl" && arguments is ["list"])
        {
            return Ok(SampleData.ListOutput);
        }

        if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.BridgeLabel}"])
        {
            return Ok(SampleData.PrintCrashLooping(SampleData.BridgeLabel, runs: 2, exitCode: 0, plistPath: SampleData.BridgePlistPath));
        }

        if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.CacheLabel}"])
        {
            return Ok(SampleData.PrintNeverExitedRunning(SampleData.CacheLabel, pid: 501, plistPath: "/Users/example/Library/LaunchAgents/cache.plist"));
        }

        if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.GhostLabel}"])
        {
            return Ok(SampleData.PrintNeverRan(SampleData.GhostLabel));
        }

        if (fileName == "/bin/sh")
        {
            return Ok("all checks passed\n");
        }

        return new CommandResult(127, string.Empty, "unexpected command", false, false);
    }

    private static CommandResult ExitedFailedWorld(string fileName, IReadOnlyList<string> arguments, int call)
    {
        if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.BridgeLabel}"])
        {
            // runs 2 with exit 78 and no rise across the resample: a one-time failure, not a loop.
            return Ok(SampleData.PrintCrashLooping(SampleData.BridgeLabel, runs: 2, exitCode: 78, plistPath: SampleData.BridgePlistPath));
        }

        return HealthyWorld(fileName, arguments, call);
    }

    private static (int Exit, string Stdout, string Stderr, FakeCommandRunner Runner, FakeClock Clock) RunApp(
        string[] args,
        Func<string, IReadOnlyList<string>, int, CommandResult> handler,
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
        Assert.Equal(JsonValueKind.Null, job.GetProperty("stderrTail").ValueKind); // empty stderr log
        var stdoutTail = job.GetProperty("stdoutTail");
        Assert.Equal("listening on 8081", stdoutTail[stdoutTail.GetArrayLength() - 1].GetString());
    }

    [Fact]
    public void CrashLoopSamplingWithDoctorContradictionExitsTwo()
    {
        CommandResult CrashWorld(string fileName, IReadOnlyList<string> arguments, int call)
        {
            if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.BridgeLabel}"])
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

        // The doctor command was run through sh exactly once for the one selected job, as one verbatim argv entry.
        Assert.Contains(runner.Invocations, i => i.FileName == "/bin/sh" && i.Arguments is ["-c", "bridge doctor"]);
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
        Func<string, IReadOnlyList<string>, int, CommandResult> TimeoutDoctor(Func<string, IReadOnlyList<string>, int, CommandResult> world) =>
            (fileName, arguments, call) =>
                fileName == "/bin/sh"
                    ? new CommandResult(null, string.Empty, string.Empty, TimedOut: true, FailedToStart: false)
                    : world(fileName, arguments, call);

        var (exit, stdout, _, _, _) = RunApp([SampleData.BridgeLabel, "--doctor", "slow doctor", "--json"], TimeoutDoctor(ExitedFailedWorld));

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit); // exited-failed still drives the exit code
        using var document = JsonDocument.Parse(stdout);
        var doctor = document.RootElement.GetProperty("jobs")[0].GetProperty("doctor");
        Assert.True(doctor.GetProperty("timedOut").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doctor.GetProperty("exitCode").ValueKind); // timed out: exit code unknown
        Assert.False(doctor.GetProperty("contradiction").GetBoolean());
    }

    [Fact]
    public void UnreadablePrintYieldsUnknownAndExitThree()
    {
        CommandResult BrokenWorld(string fileName, IReadOnlyList<string> arguments, int call)
        {
            if (fileName == "launchctl" && arguments is ["list"])
            {
                return Ok(SampleData.ListOutputNoStatus); // not even the list row has a status
            }

            if (fileName == "launchctl" && arguments is ["print", ..])
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
        Assert.Equal(JsonValueKind.Null, job.GetProperty("state").ValueKind);
        Assert.Equal(JsonValueKind.Null, job.GetProperty("runCount").ValueKind);
    }

    [Fact]
    public void UnprintableJobFallsBackToListData()
    {
        // Some system-provided jobs cannot be printed; the listing row still says whether they run.
        CommandResult UnprintableWorld(string fileName, IReadOnlyList<string> arguments, int call)
        {
            if (fileName == "launchctl" && arguments is ["list"])
            {
                return Ok(SampleData.ListOutputBridgeRunning);
            }

            if (fileName == "launchctl" && arguments is ["print", ..])
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
        Assert.Equal(JsonValueKind.Null, job.GetProperty("runCount").ValueKind); // print unreadable
        Assert.Contains("launchctl print failed", job.GetProperty("reason").GetString());
        Assert.Empty(clock.Slept); // last exit 0 from the list: nothing suspicious, no sampling
    }

    [Fact]
    public void UnprintableJobWithFailedExitIsExitedFailedFromListData()
    {
        CommandResult UnprintableWorld(string fileName, IReadOnlyList<string> arguments, int call)
        {
            if (fileName == "launchctl" && arguments is ["print", ..])
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
    public void JetsammedJobPrintsWithoutExitCodeFallsBackToListRowSignal()
    {
        // Live-observed shape: a jetsammed job's print output parses fine but carries only a
        // "last exit reason" line, never "last exit code"; the list row still shows the -9 signal.
        CommandResult JetsamWorld(string fileName, IReadOnlyList<string> arguments, int call)
        {
            if (fileName == "launchctl" && arguments is ["list"])
            {
                return Ok(SampleData.ListOutputBridgeSignalKilled);
            }

            if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.BridgeLabel}"])
            {
                return Ok(SampleData.PrintJetsammed(SampleData.BridgeLabel, runs: 3));
            }

            return HealthyWorld(fileName, arguments, call);
        }

        var (exit, stdout, _, _, _) = RunApp([SampleData.BridgeLabel, "--json"], JetsamWorld);

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit);
        using var document = JsonDocument.Parse(stdout);
        var job = document.RootElement.GetProperty("jobs")[0];
        Assert.Equal("exited-failed", job.GetProperty("verdict").GetString());
        Assert.Equal(-9, job.GetProperty("lastExitCode").GetInt32());
        Assert.Equal("SIGKILL", job.GetProperty("lastExitSignal").GetString());
        Assert.Contains("killed by SIGKILL", job.GetProperty("reason").GetString());
    }

    [Fact]
    public void RecoveredJobIsNotCrashLoopingAndExitsZero()
    {
        // A job that crash-looped 5400 times, whose process has now been up 5m30s: recovered.
        CommandResult RecoveredWorld(string fileName, IReadOnlyList<string> arguments, int call)
        {
            if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.BridgeLabel}"])
            {
                return Ok(SampleData.PrintRunningFailed(SampleData.BridgeLabel, pid: 4242, runs: 5400, exitCode: 78, plistPath: SampleData.BridgePlistPath));
            }

            if (fileName == "ps" && arguments[0] == "-o")
            {
                return Ok("05:30");
            }

            return HealthyWorld(fileName, arguments, call);
        }

        var (exit, stdout, _, runner, clock) = RunApp(
            [SampleData.BridgeLabel, "--doctor", "bridge doctor", "--json"], RecoveredWorld);

        Assert.Equal(0, exit); // recovered is not unhealthy
        using var document = JsonDocument.Parse(stdout);
        var job = document.RootElement.GetProperty("jobs")[0];
        Assert.Equal("recovered", job.GetProperty("verdict").GetString());
        Assert.Equal(5400, job.GetProperty("runCount").GetInt32());
        Assert.Contains("5400 restarts", job.GetProperty("reason").GetString());
        Assert.False(job.GetProperty("doctor").GetProperty("contradiction").GetBoolean()); // not failing now
        var summary = document.RootElement.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("recovered").GetInt32());
        Assert.Equal(0, summary.GetProperty("crashLooping").GetInt32());
        Assert.Single(clock.Slept); // still suspicious: sampled once
        Assert.Contains(runner.Invocations, i => i.FileName == "ps"); // uptime was read read-only
    }

    [Fact]
    public void FastLoopMidRunIsStillCrashLooping()
    {
        // Dies ~3s after each start, restarted every 10s, sampled mid-run: uptime 3s is below the
        // 60s recovery bound, so the threshold rule still reports an active loop.
        CommandResult FastLoopWorld(string fileName, IReadOnlyList<string> arguments, int call)
        {
            if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.BridgeLabel}"])
            {
                return Ok(SampleData.PrintRunningFailed(SampleData.BridgeLabel, pid: 999, runs: 30, exitCode: 78, plistPath: SampleData.BridgePlistPath));
            }

            if (fileName == "ps" && arguments[0] == "-o")
            {
                return Ok("00:03");
            }

            return HealthyWorld(fileName, arguments, call);
        }

        var (exit, stdout, _, _, _) = RunApp([SampleData.BridgeLabel, "--json"], FastLoopWorld);

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit);
        using var document = JsonDocument.Parse(stdout);
        var job = document.RootElement.GetProperty("jobs")[0];
        Assert.Equal("crash-looping", job.GetProperty("verdict").GetString());
    }

    [Fact]
    public void FastLoopBetweenRunsIsStillCrashLooping()
    {
        // The same loop sampled between runs: not running, no uptime to read, threshold rule fires.
        CommandResult FastLoopWorld(string fileName, IReadOnlyList<string> arguments, int call)
        {
            if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.BridgeLabel}"])
            {
                return Ok(SampleData.PrintCrashLooping(SampleData.BridgeLabel, runs: 30, exitCode: 78, plistPath: SampleData.BridgePlistPath));
            }

            return HealthyWorld(fileName, arguments, call);
        }

        var (exit, stdout, _, runner, _) = RunApp([SampleData.BridgeLabel, "--json"], FastLoopWorld);

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit);
        using var document = JsonDocument.Parse(stdout);
        var job = document.RootElement.GetProperty("jobs")[0];
        Assert.Equal("crash-looping", job.GetProperty("verdict").GetString());
        Assert.DoesNotContain(runner.Invocations, i => i.FileName == "ps"); // nothing running: no uptime read
    }

    [Fact]
    public void EveryDocumentedJsonFieldIsAlwaysEmitted()
    {
        // The JSON contract: every documented field is present on every job and the summary, with
        // null (or false) when empty - never omitted.
        var (exit, stdout, _, _, _) = RunApp([SampleData.GhostLabel, "--json"], HealthyWorld);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        foreach (var field in new[]
                 {
                     "schemaVersion", "generatedAtUtc", "domain", "selection", "jobs", "summary", "exitCode",
                 })
        {
            Assert.True(root.TryGetProperty(field, out _), $"missing top-level field {field}");
        }

        var job = root.GetProperty("jobs")[0];
        foreach (var field in new[]
                 {
                     "label", "state", "pid", "runCount", "lastExitCode", "lastExitSignal", "logPaths",
                     "stderrTail", "stdoutTail", "restartRatePerMinute", "restartIntervalSeconds",
                     "verdict", "reason", "doctor",
                 })
        {
            Assert.True(job.TryGetProperty(field, out _), $"missing job field {field}");
        }

        Assert.Equal(JsonValueKind.Null, job.GetProperty("doctor").ValueKind); // no --doctor given
        Assert.Equal(JsonValueKind.Null, job.GetProperty("lastExitCode").ValueKind); // never exited
        Assert.Equal(JsonValueKind.Null, job.GetProperty("pid").ValueKind);
        Assert.Equal(JsonValueKind.Null, job.GetProperty("logPaths").ValueKind); // plist unreadable here
        Assert.Equal(JsonValueKind.Null, job.GetProperty("stderrTail").ValueKind);

        var summary = root.GetProperty("summary");
        foreach (var field in new[]
                 {
                     "total", "healthy", "crashLooping", "recovered", "loadedNeverRan", "exitedFailed",
                     "unknown", "contradictions",
                 })
        {
            Assert.True(summary.TryGetProperty(field, out _), $"missing summary field {field}");
        }
    }

    [Fact]
    public void LaunchctlListFailureIsAReadError()
    {
        CommandResult NoList(string fileName, IReadOnlyList<string> arguments, int call) =>
            fileName == "launchctl" && arguments is ["list"]
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
