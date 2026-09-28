namespace SvcTruth;

/// <summary>Runs an external command with a timeout and captures its output. The seam for all process spawning.</summary>
public interface ICommandRunner
{
    CommandResult Run(string fileName, string arguments, TimeSpan timeout);
}

/// <summary>Outcome of one command run. ExitCode is null when the command timed out or failed to start.</summary>
public sealed record CommandResult(int? ExitCode, string Stdout, string Stderr, bool TimedOut, bool FailedToStart);

/// <summary>Read-only file access. The seam for reading plists and log tails.</summary>
public interface IFileSystem
{
    bool FileExists(string path);

    /// <summary>Reads up to <paramref name="maxBytes"/> from the start of the file, or null when missing/unreadable.</summary>
    string? TryReadText(string path, int maxBytes);

    /// <summary>
    /// Reads the last lines of a file: reads at most <paramref name="maxBytesFromEnd"/> from the end,
    /// splits into lines, and returns at most <paramref name="maxLines"/> of them (each trimmed to
    /// <paramref name="maxLineLength"/> characters). Returns null when the file is missing or unreadable,
    /// and an empty list when it exists but has no content.
    /// </summary>
    IReadOnlyList<string>? TryReadTailLines(string path, int maxBytesFromEnd, int maxLines, int maxLineLength);
}

/// <summary>Time access and waiting. The seam for the sampling delay between two observations.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Blocks for the given delay.</summary>
    void Sleep(TimeSpan delay);
}

public sealed class RealClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public void Sleep(TimeSpan delay) => Thread.Sleep(delay);
}
