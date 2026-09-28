using Xunit;

namespace SvcTruth.Tests;

/// <summary>
/// svc-truth is strictly read-only: these tests pin that guarantee. The command runner refuses every
/// state-changing launchctl subcommand, and a full run only ever asks launchctl for `list` and `print`.
/// </summary>
public class ReadOnlyGuardTests
{
    [Theory]
    [MemberData(nameof(MutatingVerbs))]
    public void RunnerRefusesMutatingLaunchctlVerbs(string verb)
    {
        Assert.Throws<InvalidOperationException>(
            () => ProcessCommandRunner.AssertReadOnlyLaunchctl("launchctl", [verb, "gui/501/com.example.job"]));
    }

    [Theory]
    [MemberData(nameof(MutatingVerbs))]
    public void RunnerRefusesMutatingLaunchctlVerbsByFullPath(string verb)
    {
        Assert.Throws<InvalidOperationException>(
            () => ProcessCommandRunner.AssertReadOnlyLaunchctl("/bin/launchctl", [verb, "gui/501/com.example.job"]));
    }

    public static TheoryData<string> MutatingVerbs =>
        new(ProcessCommandRunner.MutatingLaunchctlVerbs);

    [Fact]
    public void RunnerAllowsReadOnlyLaunchctlSubcommands()
    {
        ProcessCommandRunner.AssertReadOnlyLaunchctl("launchctl", ["list"]); // must not throw
        ProcessCommandRunner.AssertReadOnlyLaunchctl("launchctl", ["print", "gui/501/com.example.job"]); // must not throw
    }

    [Fact]
    public void RunnerDoesNotGuardNonLaunchctlPrograms()
    {
        // The --doctor path legitimately runs user-supplied commands through sh.
        ProcessCommandRunner.AssertReadOnlyLaunchctl("/bin/sh", ["-c", "launchctl kickstart gui/501/x"]); // must not throw
    }

    [Fact]
    public void RealRunnerThrowsInsteadOfRunningAMutatingVerb()
    {
        var runner = new ProcessCommandRunner();
        Assert.Throws<InvalidOperationException>(
            () => runner.Run("launchctl", ["bootout", "gui/501/com.example.job"], TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FullRunOnlyEverIssuesReadOnlyLaunchctlSubcommands()
    {
        CommandResult Handler(string fileName, IReadOnlyList<string> arguments, int call)
        {
            if (fileName == "launchctl" && arguments is ["list"])
            {
                return new CommandResult(0, SampleData.ListOutput, string.Empty, false, false);
            }

            if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.BridgeLabel}"])
            {
                // Suspicious on purpose so the app also performs a resample during this run.
                var runs = call == 1 ? 120 : 124;
                return new CommandResult(
                    0,
                    SampleData.PrintCrashLooping(SampleData.BridgeLabel, runs, exitCode: 78, plistPath: SampleData.BridgePlistPath),
                    string.Empty,
                    false,
                    false);
            }

            if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.CacheLabel}"])
            {
                return new CommandResult(
                    0,
                    SampleData.PrintNeverExitedRunning(SampleData.CacheLabel, pid: 501, plistPath: "/Users/example/Library/LaunchAgents/cache.plist"),
                    string.Empty,
                    false,
                    false);
            }

            if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.GhostLabel}"])
            {
                return new CommandResult(0, SampleData.PrintNeverRan(SampleData.GhostLabel), string.Empty, false, false);
            }

            if (fileName == "/bin/sh")
            {
                return new CommandResult(0, "all checks passed\n", string.Empty, false, false);
            }

            return new CommandResult(127, string.Empty, "unexpected", false, false);
        }

        var runner = new FakeCommandRunner { Handler = Handler };
        var files = new Dictionary<string, string>
        {
            [SampleData.BridgePlistPath] = SampleData.BridgePlist,
            [SampleData.BridgeStderrPath] = SampleData.BridgeStderrTail,
        };
        var exit = await SvcTruthApp.Run(
            ["io.github.example", "--doctor", "anything", "--json"],
            501,
            runner,
            new FakeFileSystem(files),
            new FakeClock(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)),
            new StringWriter(),
            new StringWriter());

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit);
        Assert.NotEmpty(runner.LaunchctlArguments);
        Assert.All(runner.LaunchctlArguments, arguments =>
        {
            Assert.True(
                arguments is ["list"] or ["print", ..],
                $"svc-truth must only issue read-only launchctl subcommands, but asked for: launchctl {string.Join(' ', arguments)}");
        });
        Assert.Contains(runner.LaunchctlArguments, a => a is ["print", ..]); // print happened, including a resample
    }
}
