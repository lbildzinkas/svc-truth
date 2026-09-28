using System.Text.Json;

namespace SvcTruth;

/// <summary>Renders the final report as short human-readable lines or as one stable JSON object.</summary>
public static class ReportWriter
{
    public static string Json(SvcTruthReport report)
    {
        return JsonSerializer.Serialize(report, ReportJsonContext.Default.SvcTruthReport);
    }

    /// <summary>Listing (more than one job): one short line per job. Detail (single job): a full block with paths, tails and doctor.
    /// In a listing, loaded-never-ran jobs collapse into one summary line unless <paramref name="showAll"/> is set; their verdict never fails the run either way.</summary>
    public static void Human(SvcTruthReport report, TextWriter writer, bool showAll)
    {
        writer.WriteLine(
            $"domain {report.Domain}: {report.Summary.Total} job(s){(report.Selection is null ? string.Empty : $" matching \"{report.Selection}\"")}");

        var detail = report.Jobs.Count == 1;
        var shownJobs = showAll || detail ? report.Jobs : report.Jobs.Where(j => j.Verdict != "loaded-never-ran").ToList();
        var collapsed = report.Jobs.Count - shownJobs.Count;
        var indent = detail ? "        " : string.Empty;
        foreach (var job in shownJobs)
        {
            writer.WriteLine();
            if (detail)
            {
                writer.WriteLine(job.Label);
                writer.WriteLine($"{indent}{JobLine(job)}");
            }
            else
            {
                var contradiction = job.Doctor?.Contradiction ?? false;
                writer.WriteLine($"{job.Label}  {JobLine(job)}{(contradiction ? "  CONTRADICTION" : string.Empty)}");
                if (job.Log?.Error is { } logError)
                {
                    writer.WriteLine($"  log: {job.Log.Path}  ({logError})");
                }

                foreach (var tailLine in job.Log?.Tail ?? [])
                {
                    writer.WriteLine($"  {tailLine}");
                }
            }

            if (!detail)
            {
                continue;
            }

            if (job.Reason is not null)
            {
                writer.WriteLine($"{indent}{job.Reason}");
            }

            if (job.LogPaths?.Stderr is not null)
            {
                writer.WriteLine($"{indent}stderr: {job.LogPaths.Stderr}");
            }

            if (job.LogPaths?.Stdout is not null)
            {
                writer.WriteLine($"{indent}stdout: {job.LogPaths.Stdout}");
            }

            if (job.StderrTail is { Count: > 0 })
            {
                writer.WriteLine($"{indent}stderr tail:");
                foreach (var tailLine in job.StderrTail)
                {
                    writer.WriteLine($"{indent}  {tailLine}");
                }
            }
            else if (job.StdoutTail is { Count: > 0 })
            {
                writer.WriteLine($"{indent}stdout tail:");
                foreach (var tailLine in job.StdoutTail)
                {
                    writer.WriteLine($"{indent}  {tailLine}");
                }
            }

            if (job.Log is not null)
            {
                writer.WriteLine(
                    $"{indent}log: {job.Log.Path}{(job.Log.Error is null ? string.Empty : $"  ({job.Log.Error})")}");
                foreach (var tailLine in job.Log.Tail ?? [])
                {
                    writer.WriteLine($"{indent}  {tailLine}");
                }
            }

            if (job.Doctor is not null)
            {
                var exitText = job.Doctor.TimedOut
                    ? "timed out"
                    : job.Doctor.ExitCode?.ToString() ?? "failed to start";
                writer.WriteLine(
                    $"{indent}doctor: exit {exitText}{(job.Doctor.Contradiction ? "  CONTRADICTION: doctor reports healthy while launchd disagrees" : string.Empty)}");
                foreach (var tailLine in job.Doctor.StderrTail ?? [])
                {
                    writer.WriteLine($"{indent}  {tailLine}");
                }

                foreach (var tailLine in job.Doctor.StdoutTail ?? [])
                {
                    writer.WriteLine($"{indent}  {tailLine}");
                }
            }
        }

        if (collapsed > 0)
        {
            if (shownJobs.Count > 0)
            {
                writer.WriteLine();
            }

            writer.WriteLine($"… {collapsed} loaded-never-ran job(s) not shown (pass --all to list them)");
        }
    }

    private static string JobLine(JobReport job)
    {
        var rate = job.RestartRatePerMinute is null
            ? string.Empty
            : $"  restart ~{Rounded(job.RestartRatePerMinute.Value)}/min";
        return
            $"{OrDash(job.State),12}  pid {FormatPid(job.Pid),7}  runs {OrDash(job.RunCount),5}  last-exit {FormatExit(job),12}  {job.Verdict.ToUpperInvariant()}{rate}";
    }

    private static string OrDash(object? value) => value?.ToString() ?? "-";

    private static string FormatPid(int? pid) => pid?.ToString() ?? "-";

    private static string FormatExit(JobReport job)
    {
        if (job.LastExitCode is null)
        {
            return "never";
        }

        if (job.LastExitCode < 0)
        {
            return job.LastExitSignal ?? job.LastExitCode.Value.ToString();
        }

        return job.LastExitCode.Value.ToString();
    }

    private static string Rounded(double value) =>
        Math.Round(value, 1).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
}
