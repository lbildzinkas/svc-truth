using SvcTruth.Launchd;

namespace SvcTruth;

public enum Verdict
{
    Healthy,
    CrashLooping,
    LoadedNeverRan,
    ExitedFailed,
    Unknown,
}

/// <summary>Tunable verdict thresholds; the defaults are documented in the README.</summary>
public sealed record VerdictOptions(int SampleRiseCount = 3, int RunCountThreshold = 10)
{
    /// <summary>Delay between the two samples used to detect a rising run count.</summary>
    public static readonly TimeSpan SampleDelay = TimeSpan.FromSeconds(3);
}

/// <summary>One observation of a job, fused from <c>launchctl list</c> (always available) and <c>launchctl print</c> (when readable). LastExitStatus follows launchctl's convention: negative means killed by that signal, null means never exited or unknown. RunCount is null when the job's print output is unreadable.</summary>
public sealed record JobSample(string Label, string? State, int? Pid, long? RunCount, int? LastExitStatus, bool NeverExited);

/// <summary>The second observation plus how much time passed between the two samples.</summary>
public sealed record Resample(JobSample Sample, TimeSpan Elapsed);

/// <summary>The verdict for one job plus the measured restart rate when sampling produced one.</summary>
public sealed record VerdictResult(Verdict Verdict, double? RestartRatePerMinute, double? RestartIntervalSeconds, string Reason)
{
    public static VerdictResult Of(Verdict verdict, string reason) => new(verdict, null, null, reason);
}

/// <summary>
/// Applies the verdict rules documented in the README. Pure: both samples are passed in, so the rules are
/// unit-testable without touching launchd.
/// </summary>
public static class VerdictEngine
{
    public static VerdictResult Evaluate(JobSample first, Resample? second, VerdictOptions options)
    {
        if (first.RunCount == 0)
        {
            return VerdictResult.Of(Verdict.LoadedNeverRan, "loaded but has never run");
        }

        if (first.LastExitStatus is null)
        {
            if (IsRunning(first))
            {
                return VerdictResult.Of(
                    Verdict.Healthy,
                    first.NeverExited ? "running, has not exited yet" : "running, last exit status unavailable");
            }

            // Active-but-not-running states like "spawn scheduled": loaded, launchd plans the next run.
            if (first.NeverExited && IsActiveState(first.State))
            {
                return VerdictResult.Of(Verdict.Healthy, $"active ({first.State}), has not exited yet");
            }

            return VerdictResult.Of(Verdict.Unknown, "no usable last exit status");
        }

        var exit = first.LastExitStatus.Value;
        if (exit == 0)
        {
            return VerdictResult.Of(
                Verdict.Healthy,
                IsRunning(first) ? "running, last exit was clean" : "not running, last exit was clean");
        }

        // Non-zero last exit: suspicious, so a second sample may have been taken.
        if (second is not null)
        {
            long? delta = first.RunCount is not null && second.Sample.RunCount is not null
                ? second.Sample.RunCount - first.RunCount
                : null;
            if (delta is not null && delta >= options.SampleRiseCount)
            {
                var minutes = second.Elapsed.TotalMinutes;
                var rate = minutes > 0 ? delta.Value / minutes : (double?)null;
                var interval = delta > 0 ? second.Elapsed.TotalSeconds / delta.Value : (double?)null;
                return new VerdictResult(
                    Verdict.CrashLooping,
                    rate,
                    interval,
                    $"run count rose by {delta} in {Math.Round(second.Elapsed.TotalSeconds, 1)}s with {SignalNames.Describe(exit)}");
            }
        }

        if (first.RunCount is not null && first.RunCount >= options.RunCountThreshold)
        {
            return VerdictResult.Of(
                Verdict.CrashLooping,
                $"run count {first.RunCount} is at or above the threshold {options.RunCountThreshold} with {SignalNames.Describe(exit)}");
        }

        if (IsRunning(first))
        {
            return VerdictResult.Of(Verdict.Healthy, $"running now; previous run ended with {SignalNames.Describe(exit)}");
        }

        return VerdictResult.Of(Verdict.ExitedFailed, $"not running, last run ended with {SignalNames.Describe(exit)}");
    }

    private static bool IsRunning(JobSample sample) =>
        string.Equals(sample.State, "running", StringComparison.OrdinalIgnoreCase) || sample.Pid is not null;

    private static bool IsActiveState(string? state) =>
        state is not null && !string.Equals(state, "not running", StringComparison.OrdinalIgnoreCase);
}
