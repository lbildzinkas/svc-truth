using System.Collections.Concurrent;
using SvcTruth.Launchd;

namespace SvcTruth;

/// <summary>Command-line options. Label is either an exact label (detailed view) or a prefix (filtered listing).</summary>
public sealed record CliOptions(string? Label = null, string? DoctorCommand = null, bool Json = false)
{
    public static readonly string Usage =
        """
        svc-truth [label] [--doctor "<command>"] [--json]

        Read-only truth about the current user's launchd jobs (gui domain): state, run count,
        last exit code, log tails and a crash-loop verdict. Never starts, stops or edits anything.

          label            an exact label (one job, detailed view) or a label prefix (filtered listing);
                           omit it to list every loaded job
          --doctor CMD     also run CMD through sh with a 10s timeout for each selected job and flag a
                           contradiction when it exits 0 while launchd shows crash-looping or exited-failed
          --json           print one stable JSON object (schemaVersion 1) instead of human-readable lines
          --help           show this help
          --version        print the version

        Exit codes:
          0  every selected job is healthy
          2  at least one selected job is crash-looping, exited-failed, or contradicted by its doctor
          3  usage error or read error (launchd unreadable, no match, unknown verdict)
        """;
}

/// <summary>
/// Orchestrates one svc-truth run. All effects go through the injected seams (command runner, filesystem,
/// clock) so tests drive the whole app with captured launchctl output.
/// </summary>
public static class SvcTruthApp
{
    public const int ExitHealthy = 0;
    public const int ExitUnhealthy = 2;
    public const int ExitUsageOrReadError = 3;

    private static readonly TimeSpan LaunchctlTimeout = TimeSpan.FromSeconds(10);
    private static readonly int MaxParallelPrints = 8;
    private const int PlistMaxBytes = 1024 * 1024;
    private const int TailMaxBytesFromEnd = 64 * 1024;
    private const int TailLines = 5;
    private const int TailMaxLineLength = 400;

    public static async Task<int> Run(
        string[] args,
        uint uid,
        ICommandRunner commandRunner,
        IFileSystem fileSystem,
        IClock clock,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (!TryParseArguments(args, out var options, out var parseError))
        {
            stderr.WriteLine($"svc-truth: {parseError}");
            stderr.WriteLine(CliOptions.Usage);
            return ExitUsageOrReadError;
        }

        if (options is null)
        {
            if (args.Contains("--version"))
            {
                stdout.WriteLine(VersionText);
            }
            else
            {
                stdout.WriteLine(CliOptions.Usage);
            }

            return ExitHealthy;
        }

        var domain = $"gui/{uid}";
        var verdictOptions = new VerdictOptions();

        var listResult = commandRunner.Run("launchctl", ["list"], LaunchctlTimeout);
        if (listResult.FailedToStart || listResult.TimedOut || listResult.ExitCode is not 0)
        {
            stderr.WriteLine("svc-truth: could not read the gui domain with `launchctl list`" +
                ShortFailure(listResult));
            return ExitUsageOrReadError;
        }

        var entries = LaunchctlListParser.Parse(listResult.Stdout);
        if (entries.Count == 0)
        {
            stderr.WriteLine("svc-truth: `launchctl list` returned no jobs; output was unreadable");
            return ExitUsageOrReadError;
        }

        var selected = SelectEntries(entries, options.Label);
        if (selected.Count == 0)
        {
            stderr.WriteLine($"svc-truth: no jobs match \"{options.Label}\"");
            return ExitUsageOrReadError;
        }

        var labels = selected.Select(e => e.Label).ToList();

        // First sample: launchctl print for every selected job, in parallel, fused with the always-available list row.
        var (firstPrints, firstSampledAt) = await PrintJobsAsync(commandRunner, domain, labels, clock);
        var listByLabel = selected.ToDictionary(e => e.Label);
        var firstSamples = labels.ToDictionary(
            label => label,
            label => ToSample(label, listByLabel.GetValueOrDefault(label), firstPrints.GetValueOrDefault(label)));

        // Resample only suspicious jobs (non-zero last exit): one shared delay keeps listings fast.
        Dictionary<string, JobSample> secondSamples = [];
        DateTimeOffset secondSampledAt = firstSampledAt;
        var suspicious = labels.Where(l => IsSuspicious(firstSamples[l])).ToList();
        if (suspicious.Count > 0)
        {
            clock.Sleep(VerdictOptions.SampleDelay);
            var (secondPrints, secondAt) = await PrintJobsAsync(commandRunner, domain, suspicious, clock);
            secondSampledAt = secondAt;
            foreach (var label in suspicious)
            {
                secondSamples[label] = ToSample(label, listByLabel.GetValueOrDefault(label), secondPrints.GetValueOrDefault(label));
            }
        }

        var jobs = new List<JobReport>();
        foreach (var label in labels)
        {
            jobs.Add(BuildJobReport(
                label,
                firstPrints.GetValueOrDefault(label),
                firstSamples[label],
                secondSamples.GetValueOrDefault(label) ?? firstSamples[label],
                secondSampledAt - firstSampledAt,
                verdictOptions,
                options.DoctorCommand,
                commandRunner,
                fileSystem));
        }

        var exitCode = ResolveExitCode(jobs);
        var report = BuildReport(domain, options.Label, jobs, exitCode);
        if (options.Json)
        {
            stdout.WriteLine(ReportWriter.Json(report));
        }
        else
        {
            ReportWriter.Human(report, stdout);
        }

        return exitCode;
    }

    private static string VersionText =>
        $"svc-truth {typeof(SvcTruthApp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";

    internal static bool TryParseArguments(string[] args, out CliOptions? options, out string error)
    {
        string? label = null;
        string? doctor = null;
        var json = false;
        var wantHelp = false;
        var wantVersion = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h":
                    wantHelp = true;
                    break;
                case "--version":
                    wantVersion = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--doctor":
                    if (i + 1 >= args.Length)
                    {
                        options = null;
                        error = "--doctor needs a command, for example --doctor \"mytool doctor\"";
                        return false;
                    }

                    doctor = args[++i];
                    if (doctor.Length == 0)
                    {
                        options = null;
                        error = "--doctor needs a non-empty command";
                        return false;
                    }

                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        options = null;
                        error = $"unknown option \"{arg}\"";
                        return false;
                    }

                    if (label is not null)
                    {
                        options = null;
                        error = "only one label (prefix or exact) may be given";
                        return false;
                    }

                    label = arg;
                    break;
            }
        }

        if (wantHelp || wantVersion)
        {
            options = null; // caller prints usage/version
            error = string.Empty;
            return true;
        }

        options = new CliOptions(label, doctor, json);
        error = string.Empty;
        return true;
    }

    private static IReadOnlyList<ListEntry> SelectEntries(IReadOnlyList<ListEntry> entries, string? label)
    {
        if (label is null)
        {
            return entries;
        }

        if (entries.Any(e => e.Label == label))
        {
            return entries.Where(e => e.Label == label).ToList(); // exact label: detailed single-job view
        }

        return entries.Where(e => e.Label.StartsWith(label, StringComparison.Ordinal)).ToList();
    }

    private static async Task<(ConcurrentDictionary<string, JobPrintInfo?> Prints, DateTimeOffset SampledAt)> PrintJobsAsync(
        ICommandRunner runner,
        string domain,
        IReadOnlyList<string> labels,
        IClock clock)
    {
        var results = new ConcurrentDictionary<string, JobPrintInfo?>();
        await Parallel.ForEachAsync(
            labels,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelPrints },
            (label, _) =>
            {
                var result = runner.Run("launchctl", ["print", $"{domain}/{label}"], LaunchctlTimeout);
                results[label] = result.ExitCode is 0 && !result.TimedOut
                    ? LaunchctlPrintParser.Parse(result.Stdout)
                    : null;
                return ValueTask.CompletedTask;
            });
        return (results, clock.UtcNow);
    }

    /// <summary>
    /// Fuses the always-available list row (pid, last exit status) with the print fields when the print is
    /// readable. Some system-provided jobs cannot be printed; for those the list row still carries the
    /// essentials, so the verdict degrades gracefully instead of collapsing to unknown.
    /// </summary>
    private static JobSample ToSample(string label, ListEntry? listEntry, JobPrintInfo? print) =>
        print is not null
            ? new JobSample(label, print.State, print.Pid, print.Runs, print.LastExitCode, print.NeverExited)
            : new JobSample(
                label,
                listEntry?.Pid is not null ? "running" : null,
                listEntry?.Pid,
                RunCount: null,
                listEntry?.LastExitStatus,
                NeverExited: false);

    private static bool IsSuspicious(JobSample sample) =>
        sample.LastExitStatus is not null && sample.LastExitStatus != 0;

    private static JobReport BuildJobReport(
        string label,
        JobPrintInfo? print,
        JobSample first,
        JobSample second,
        TimeSpan sampleSpan,
        VerdictOptions verdictOptions,
        string? doctorCommand,
        ICommandRunner commandRunner,
        IFileSystem fileSystem)
    {
        VerdictResult verdict = VerdictEngine.Evaluate(
            first,
            IsSuspicious(first) && sampleSpan > TimeSpan.Zero ? new Resample(second, sampleSpan) : null,
            verdictOptions);
        var reason = print is null
            ? verdict.Reason + "; job details unreadable (launchctl print failed, judged from launchctl list)"
            : verdict.Reason;

        // Read the declared log paths from the job's plist when we know where it is.
        LogPathsReport? logPaths = null;
        if (print?.PlistPath is { } plistPath)
        {
            var plistXml = fileSystem.TryReadText(plistPath, PlistMaxBytes);
            var (stdoutPath, stderrPath) = PlistParser.GetLogPaths(plistXml);
            logPaths = stdoutPath is null && stderrPath is null
                ? null
                : new LogPathsReport { Stdout = stdoutPath, Stderr = stderrPath };
        }

        IReadOnlyList<string>? stderrTail = null;
        IReadOnlyList<string>? stdoutTail = null;
        var stderrLogPath = logPaths?.Stderr;
        if (stderrLogPath is not null)
        {
            stderrTail = fileSystem.TryReadTailLines(stderrLogPath, TailMaxBytesFromEnd, TailLines, TailMaxLineLength);
        }

        if (stderrTail is null || stderrTail.Count == 0)
        {
            var stdoutLogPath = logPaths?.Stdout;
            if (stdoutLogPath is not null)
            {
                stdoutTail = fileSystem.TryReadTailLines(stdoutLogPath, TailMaxBytesFromEnd, TailLines, TailMaxLineLength);
            }
        }

        DoctorReport? doctor = null;
        if (doctorCommand is not null)
        {
            doctor = DoctorRunner.Run(doctorCommand, commandRunner);
            doctor = doctor with
            {
                Contradiction = doctor.ExitCode == 0
                    && verdict.Verdict is Verdict.CrashLooping or Verdict.ExitedFailed,
            };
        }

        var lastExitSignal = first.LastExitStatus is < 0 ? SignalNames.NameFor(-first.LastExitStatus.Value) : null;

        return new JobReport
        {
            Label = label,
            State = first.State,
            Pid = first.Pid,
            RunCount = first.RunCount,
            LastExitCode = first.LastExitStatus,
            LastExitSignal = lastExitSignal,
            LogPaths = logPaths,
            StderrTail = stderrTail is { Count: > 0 } ? stderrTail : null,
            StdoutTail = stdoutTail is { Count: > 0 } ? stdoutTail : null,
            RestartRatePerMinute = verdict.RestartRatePerMinute,
            RestartIntervalSeconds = verdict.RestartIntervalSeconds,
            Verdict = VerdictToString(verdict.Verdict),
            Reason = reason,
            Doctor = doctor,
        };
    }

    private static string VerdictToString(Verdict verdict) => verdict switch
    {
        Verdict.Healthy => "healthy",
        Verdict.CrashLooping => "crash-looping",
        Verdict.LoadedNeverRan => "loaded-never-ran",
        Verdict.ExitedFailed => "exited-failed",
        _ => "unknown",
    };

    private static int ResolveExitCode(IReadOnlyList<JobReport> jobs)
    {
        if (jobs.Any(j => j.Verdict is "crash-looping" or "exited-failed" || (j.Doctor?.Contradiction ?? false)))
        {
            return ExitUnhealthy;
        }

        if (jobs.Any(j => j.Verdict == "unknown"))
        {
            return ExitUsageOrReadError;
        }

        return ExitHealthy;
    }

    private static SvcTruthReport BuildReport(string domain, string? selection, List<JobReport> jobs, int exitCode)
    {
        var summary = new SummaryReport
        {
            Total = jobs.Count,
            Healthy = jobs.Count(j => j.Verdict == "healthy"),
            CrashLooping = jobs.Count(j => j.Verdict == "crash-looping"),
            LoadedNeverRan = jobs.Count(j => j.Verdict == "loaded-never-ran"),
            ExitedFailed = jobs.Count(j => j.Verdict == "exited-failed"),
            Unknown = jobs.Count(j => j.Verdict == "unknown"),
            Contradictions = jobs.Count(j => j.Doctor?.Contradiction ?? false),
        };

        return new SvcTruthReport
        {
            SchemaVersion = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Domain = domain,
            Selection = selection,
            Jobs = jobs,
            Summary = summary,
            ExitCode = exitCode,
        };
    }

    private static string ShortFailure(CommandResult result)
    {
        if (result.TimedOut)
        {
            return ": timed out";
        }

        if (result.FailedToStart)
        {
            return ": launchctl could not be started";
        }

        var firstLine = result.Stderr.Split('\n').FirstOrDefault(l => l.Length > 0);
        return firstLine is null ? $": exit code {result.ExitCode}" : $": {firstLine}";
    }
}
