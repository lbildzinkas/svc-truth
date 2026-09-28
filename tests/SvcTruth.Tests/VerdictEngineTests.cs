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
        bool neverExited = false,
        TimeSpan? processUptime = null) =>
        new(label, state, pid, runs, lastExit, neverExited, processUptime);

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
    public void HighRestartsWhileRunningStablyIsRecovered()
    {
        // A job that crash-looped 5400 times but whose process has now been up for 2 minutes has
        // broken the loop: recovered, with the past restart count in the reason.
        var result = VerdictEngine.Evaluate(
            Sample(runs: 5400, lastExit: 78, processUptime: TimeSpan.FromSeconds(120)), null, Options);

        Assert.Equal(Verdict.Recovered, result.Verdict);
        Assert.Contains("5400 restarts", result.Reason);
    }

    [Fact]
    public void FastLoopMidRunIsStillCrashLooping()
    {
        // Dies ~3s after each start, restarted every 10s, sampled mid-run: the current process has
        // only been up 3s, far below the recovery bound, so the threshold rule still fires.
        var result = VerdictEngine.Evaluate(
            Sample(runs: 30, lastExit: 78, processUptime: TimeSpan.FromSeconds(3)), null, Options);

        Assert.Equal(Verdict.CrashLooping, result.Verdict);
    }

    [Fact]
    public void FastLoopBetweenRunsIsStillCrashLooping()
    {
        // The same loop sampled between runs: not running, threshold rule fires.
        var result = VerdictEngine.Evaluate(
            Sample(runs: 30, state: "not running", pid: null, lastExit: 78), null, Options);

        Assert.Equal(Verdict.CrashLooping, result.Verdict);
    }

    [Fact]
    public void ObservedRiseDuringSamplingBeatsRecovery()
    {
        // A run count that rose during the 3s sampling window means restarts are happening right now;
        // that reading wins even if the (contradictory) uptime says the process is old.
        var first = Sample(runs: 5400, lastExit: 78, processUptime: TimeSpan.FromSeconds(120));
        var second = new Resample(Sample(runs: 5404), TimeSpan.FromSeconds(3));

        var result = VerdictEngine.Evaluate(first, second, Options);

        Assert.Equal(Verdict.CrashLooping, result.Verdict);
    }

    [Fact]
    public void FastLoopMidRunWithObservedRiseIsStillCrashLooping()
    {
        var first = Sample(runs: 30, lastExit: 78, processUptime: TimeSpan.FromSeconds(2));
        var second = new Resample(Sample(runs: 33), TimeSpan.FromSeconds(3));

        var result = VerdictEngine.Evaluate(first, second, Options);

        Assert.Equal(Verdict.CrashLooping, result.Verdict);
        Assert.NotNull(result.RestartRatePerMinute);
    }

    [Fact]
    public void UnknownUptimeKeepsCrashLooping()
    {
        // Cannot prove stability (ps unreadable): the alerting verdict stands.
        var result = VerdictEngine.Evaluate(
            Sample(runs: 5400, lastExit: 78, processUptime: null), null, Options);

        Assert.Equal(Verdict.CrashLooping, result.Verdict);
    }

    [Fact]
    public void LowRestartsWhileRunningStablyStaysHealthy()
    {
        // Below the crash-loop threshold there was never a loop to recover from.
        var result = VerdictEngine.Evaluate(
            Sample(runs: 2, lastExit: 78, processUptime: TimeSpan.FromSeconds(120)), null, Options);

        Assert.Equal(Verdict.Healthy, result.Verdict);
    }

    [Fact]
    public void CustomRecoveryUptimeIsHonoured()
    {
        var options = new VerdictOptions(RecoveryUptime: TimeSpan.FromSeconds(300));
        var result = VerdictEngine.Evaluate(
            Sample(runs: 5400, lastExit: 78, processUptime: TimeSpan.FromSeconds(120)), null, options);

        Assert.Equal(Verdict.CrashLooping, result.Verdict);
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
