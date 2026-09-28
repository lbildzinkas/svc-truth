using System.Text.Json.Serialization;

namespace SvcTruth;

/// <summary>The single JSON object --json prints. Fields are camelCase and stable across patch releases; the schemaVersion field guards breaking changes. Every documented field is always emitted — empty values serialize as null (or false for booleans) rather than being omitted.</summary>
public sealed record SvcTruthReport
{
    public int SchemaVersion { get; init; } = 1;

    public string GeneratedAtUtc { get; init; } = string.Empty;

    /// <summary>The launchd domain that was read, for example "gui/501".</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>The label argument (prefix or exact label), or null when all jobs were selected.</summary>
    public string? Selection { get; init; }

    public IReadOnlyList<JobReport> Jobs { get; init; } = [];

    public SummaryReport Summary { get; init; } = new();

    /// <summary>The process exit code this report maps to (see the README's exit-code table).</summary>
    public int ExitCode { get; init; }
}

public sealed record JobReport
{
    public string Label { get; init; } = string.Empty;

    /// <summary>launchd state, for example "running" or "not running"; null when unreadable.</summary>
    public string? State { get; init; }

    /// <summary>Current pid, or null when the job is not running.</summary>
    public int? Pid { get; init; }

    /// <summary>launchd's run count; null when unreadable.</summary>
    public long? RunCount { get; init; }

    /// <summary>launchd's last exit status; negative = killed by that signal; null = never exited or unknown.</summary>
    public int? LastExitCode { get; init; }

    /// <summary>Signal name (SIGTERM, ...) when the last run died by signal, else null.</summary>
    public string? LastExitSignal { get; init; }

    public LogPathsReport? LogPaths { get; init; }

    /// <summary>Tail of the stderr log (the stdout tail is used when stderr is empty), trimmed to a few lines.</summary>
    public IReadOnlyList<string>? StderrTail { get; init; }

    /// <summary>Tail of the stdout log, non-null only when the stderr log is empty.</summary>
    public IReadOnlyList<string>? StdoutTail { get; init; }

    /// <summary>The file named with --log: null without the option; otherwise its tail (or why it could not be read).</summary>
    public LogFileReport? Log { get; init; }

    /// <summary>Restarts per minute measured across the two samples; null unless a rate was measured.</summary>
    public double? RestartRatePerMinute { get; init; }

    /// <summary>Average seconds between restarts measured across the two samples; null unless measured.</summary>
    public double? RestartIntervalSeconds { get; init; }

    public string Verdict { get; init; } = string.Empty;

    /// <summary>Short human-readable explanation of why this verdict was chosen.</summary>
    public string? Reason { get; init; }

    public DoctorReport? Doctor { get; init; }
}

public sealed record LogPathsReport
{
    public string? Stdout { get; init; }

    public string? Stderr { get; init; }
}

/// <summary>The extra log file named with --log, shown for every selected job.</summary>
public sealed record LogFileReport
{
    /// <summary>The path actually read (a leading ~ already expanded to the home directory).</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Last lines of the file, trimmed like the other tails; null when it has no content or could not be read.</summary>
    public IReadOnlyList<string>? Tail { get; init; }

    /// <summary>Why the tail is missing, for example "file not found"; null when the file was read.</summary>
    public string? Error { get; init; }
}

public sealed record SummaryReport
{
    public int Total { get; init; }

    public int Healthy { get; init; }

    public int CrashLooping { get; init; }

    public int Recovered { get; init; }

    public int LoadedNeverRan { get; init; }

    public int ExitedFailed { get; init; }

    public int Unknown { get; init; }

    public int Contradictions { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SvcTruthReport))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;
