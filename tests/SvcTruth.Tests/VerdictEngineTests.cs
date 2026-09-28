using Xunit;

namespace SvcTruth.Tests;

public class VerdictEngineTests
{
    private static readonly VerdictOptions Options = new();

    private static JobSample Sample(
        string label = "job",
        string? state = "running",
        int? pid = 123,
        long? runs = 4,
        int? lastExit = 0,
        bool neverExited = false) =>
        new(label, state, pid, runs, lastExit, neverExited);

    [Fact]
    public void RunningWithCleanExitIsHealthy()
    {
        var result = VerdictEngine.Evaluate(Sample(), null, Options);

        Assert.Equal(Verdict.Healthy, result.Verdict);
        Assert.Null(result.RestartRatePerMinute);
    }

    [Fact]
    public void NotRunningWithCleanExitIsHealthy()
    {
        var result = VerdictEngine.Evaluate(Sample(state: "not running", pid: null, lastExit: 0), null, Options);

        Assert.Equal(Verdict.Healthy, result.Verdict);
    }

    [Fact]
    public void ZeroRunsMeansLoadedNeverRan()
    {
        var result = VerdictEngine.Evaluate(
            Sample(runs: 0, state: "not running", pid: null, lastExit: null, neverExited: true), null, Options);

        Assert.Equal(Verdict.LoadedNeverRan, result.Verdict);
    }

    [Fact]
    public void RunningNeverExitedIsHealthy()
    {
        var result = VerdictEngine.Evaluate(
            Sample(runs: 1, lastExit: null, neverExited: true), null, Options);

        Assert.Equal(Verdict.Healthy, result.Verdict);
    }

    [Fact]
    public void NotRunningNeverExitedIsUnknown()
    {
        var result = VerdictEngine.Evaluate(
            Sample(runs: 1, state: "not running", pid: null, lastExit: null, neverExited: true), null, Options);

        Assert.Equal(Verdict.Unknown, result.Verdict);
    }

    [Fact]
    public void RisingRunCountAcrossSamplesIsCrashLoopingWithRate()
    {
        var first = Sample(runs: 120, state: "not running", pid: null, lastExit: 78);
        var second = new Resample(Sample(runs: 124), TimeSpan.FromSeconds(3));

        var result = VerdictEngine.Evaluate(first, second, Options);

        Assert.Equal(Verdict.CrashLooping, result.Verdict);
        Assert.NotNull(result.RestartRatePerMinute);
        Assert.Equal(80.0, result.RestartRatePerMinute!.Value, 1);
        Assert.Equal(0.75, result.RestartIntervalSeconds!.Value, 2);
    }

    [Fact]
    public void RunCountBelowRiseAcrossSamplesIsNotCrashLooping()
    {
        var first = Sample(runs: 3, state: "not running", pid: null, lastExit: 78);
        var second = new Resample(Sample(runs: 4), TimeSpan.FromSeconds(3)); // delta 1 < 3

        var result = VerdictEngine.Evaluate(first, second, Options);

        Assert.Equal(Verdict.ExitedFailed, result.Verdict);
    }

    [Fact]
    public void HighRunCountWithNonZeroExitIsCrashLoopingWithoutSampling()
    {
        var first = Sample(runs: 5400, state: "not running", pid: null, lastExit: 78);

        var result = VerdictEngine.Evaluate(first, null, Options);

        Assert.Equal(Verdict.CrashLooping, result.Verdict);
        Assert.Null(result.RestartRatePerMinute);
    }

    [Fact]
    public void NonZeroExitBelowThresholdWhenStoppedIsExitedFailed()
    {
        var result = VerdictEngine.Evaluate(
            Sample(runs: 2, state: "not running", pid: null, lastExit: 78), null, Options);

        Assert.Equal(Verdict.ExitedFailed, result.Verdict);
    }

    [Fact]
    public void DeathBySignalIsExitedFailedWithSignalInReason()
    {
        var result = VerdictEngine.Evaluate(
            Sample(runs: 2, state: "not running", pid: null, lastExit: -15), null, Options);

        Assert.Equal(Verdict.ExitedFailed, result.Verdict);
        Assert.Contains("SIGTERM", result.Reason);
    }

    [Fact]
    public void RunningNowAfterAFailedRunIsHealthy()
    {
        var first = Sample(runs: 2, state: "running", pid: 999, lastExit: 78);
        var second = new Resample(Sample(runs: 2), TimeSpan.FromSeconds(3));

        var result = VerdictEngine.Evaluate(first, second, Options);

        Assert.Equal(Verdict.Healthy, result.Verdict);
    }

    [Fact]
    public void UnknownRunCountWithRunningPidIsHealthy()
    {
        var result = VerdictEngine.Evaluate(Sample(runs: null, state: "running", pid: 501), null, Options);

        Assert.Equal(Verdict.Healthy, result.Verdict);
    }

    [Fact]
    public void UnknownRunCountWithFailedExitIsExitedFailedNotCrashLoop()
    {
        // launchctl print unreadable, list shows a failed last exit and no pid: the honest verdict is
        // exited-failed (crash-looping would claim run-count knowledge we do not have).
        var result = VerdictEngine.Evaluate(
            Sample(runs: null, state: "not running", pid: null, lastExit: 78), null, Options);

        Assert.Equal(Verdict.ExitedFailed, result.Verdict);
    }

    [Fact]
    public void UnknownRunCountWithCleanExitIsHealthy()
    {
        var result = VerdictEngine.Evaluate(
            Sample(runs: null, state: "not running", pid: null, lastExit: 0), null, Options);

        Assert.Equal(Verdict.Healthy, result.Verdict);
    }

    [Fact]
    public void RunningWithoutUsableExitStatusIsHealthy()
    {
        // e.g. a job whose print output has no "last exit code" line at all while it runs.
        var result = VerdictEngine.Evaluate(
            Sample(runs: 2, state: "running", pid: 31470, lastExit: null, neverExited: false), null, Options);

        Assert.Equal(Verdict.Healthy, result.Verdict);
    }

    [Fact]
    public void SpawnScheduledNeverExitedIsHealthy()
    {
        var result = VerdictEngine.Evaluate(
            Sample(runs: 1, state: "spawn scheduled", pid: null, lastExit: null, neverExited: true), null, Options);

        Assert.Equal(Verdict.Healthy, result.Verdict);
    }

    [Fact]
    public void CustomThresholdsAreHonoured()
    {
        var options = new VerdictOptions(SampleRiseCount: 2, RunCountThreshold: 100);
        var first = Sample(runs: 5, state: "not running", pid: null, lastExit: 1);
        var second = new Resample(Sample(runs: 7), TimeSpan.FromSeconds(3)); // delta 2

        var result = VerdictEngine.Evaluate(first, second, options);

        Assert.Equal(Verdict.CrashLooping, result.Verdict);
    }
}
