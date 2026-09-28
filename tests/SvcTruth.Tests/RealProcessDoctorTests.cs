using System.Text.Json;
using Xunit;

namespace SvcTruth.Tests;

/// <summary>
/// Exercises the --doctor path with a REAL process spawn. The launchctl answers come from captured
/// output (staging a real crash-looping job would mean mutating the gui domain, which svc-truth must
/// never do), but the doctor command runs through the real /bin/sh exactly as the shipped binary
/// runs it. This pins the argv contract end to end: the command string must reach <c>sh -c</c> as one
/// verbatim argv entry. When it was instead packed into a single Arguments string with literal shell
/// quotes, /bin/sh received the quotes as characters, failed with a syntax error (exit 2), and the
/// doctor's exit code was never 0 - so the contradiction could silently never fire.
/// </summary>
public class RealProcessDoctorTests
{
    [Fact]
    public void DoctorRunsThroughRealShAndFiresTheContradiction()
    {
        CommandResult CrashWorld(string fileName, IReadOnlyList<string> arguments, int call)
        {
            if (fileName == "launchctl" && arguments is ["list"])
            {
                return new CommandResult(0, SampleData.ListOutput, string.Empty, TimedOut: false, FailedToStart: false);
            }

            if (fileName == "launchctl" && arguments is ["print", $"gui/501/{SampleData.BridgeLabel}"])
            {
                // Second sample taken after the 3s sampling delay: run count rose by 4 -> crash-looping.
                var runs = call == 1 ? 120 : 124;
                return new CommandResult(
                    0,
                    SampleData.PrintCrashLooping(SampleData.BridgeLabel, runs, exitCode: 78, plistPath: SampleData.BridgePlistPath),
                    string.Empty,
                    TimedOut: false,
                    FailedToStart: false);
            }

            return new CommandResult(127, string.Empty, "unexpected command", TimedOut: false, FailedToStart: false);
        }

        // launchctl is faked; /bin/sh goes to the real ProcessCommandRunner, spawning a real process.
        ICommandRunner runner = new RealShRunner(CrashWorld);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = SvcTruthApp.Run(
            [SampleData.BridgeLabel, "--doctor", "echo \"self-report: it's fine\" && exit 0", "--json"],
            uid: 501,
            commandRunner: runner,
            fileSystem: new FakeFileSystem([]),
            clock: new FakeClock(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)),
            stdout: stdout,
            stderr: stderr).GetAwaiter().GetResult();

        Assert.Equal(SvcTruthApp.ExitUnhealthy, exit);
        using var document = JsonDocument.Parse(stdout.ToString());
        var job = document.RootElement.GetProperty("jobs")[0];
        Assert.Equal("crash-looping", job.GetProperty("verdict").GetString());
        var doctor = job.GetProperty("doctor");
        Assert.Equal(0, doctor.GetProperty("exitCode").GetInt32());
        Assert.Equal("self-report: it's fine", doctor.GetProperty("stdoutTail")[0].GetString());
        Assert.True(doctor.GetProperty("contradiction").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("contradictions").GetInt32());
    }

    /// <summary>Serves launchctl from the handler but runs /bin/sh (the --doctor path) as a real process.</summary>
    private sealed class RealShRunner(Func<string, IReadOnlyList<string>, int, CommandResult> handler) : ICommandRunner
    {
        private readonly ProcessCommandRunner _real = new();
        private readonly List<IReadOnlyList<string>> _fakeCalls = [];

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            if (fileName == "/bin/sh")
            {
                return _real.Run(fileName, arguments, timeout);
            }

            var call = _fakeCalls.Count(a => a.SequenceEqual(arguments)) + 1;
            _fakeCalls.Add(arguments);
            return handler(fileName, arguments, call);
        }
    }
}
