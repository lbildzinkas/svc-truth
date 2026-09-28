namespace SvcTruth;

/// <summary>Result of running the user-supplied --doctor command for one job. Contradiction is computed by the caller against that job's verdict.</summary>
public sealed record DoctorReport(
    string Command,
    int? ExitCode,
    bool TimedOut,
    IReadOnlyList<string>? StdoutTail,
    IReadOnlyList<string>? StderrTail,
    bool Contradiction);

/// <summary>Runs a --doctor command through /bin/sh with a timeout and trims its output.</summary>
public static class DoctorRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const int TailLines = 10;
    private const int MaxLineLength = 400;

    public static DoctorReport Run(string command, ICommandRunner runner)
    {
        var result = runner.Run("/bin/sh", ["-c", command], Timeout);

        var stdoutTail = Trim(result.Stdout);
        var stderrTail = Trim(result.Stderr);
        var timedOut = result.TimedOut;
        return new DoctorReport(
            command,
            result.ExitCode,
            timedOut,
            stdoutTail,
            stderrTail,
            Contradiction: false);
    }

    private static IReadOnlyList<string>? Trim(string output)
    {
        if (output.Length == 0)
        {
            return null;
        }

        var lines = output.Split('\n');
        var tail = new List<string>();
        for (var i = lines.Length - 1; i >= 0 && tail.Count < TailLines; i--)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 && i == lines.Length - 1)
            {
                continue;
            }

            tail.Add(line.Length <= MaxLineLength ? line : line[..MaxLineLength]);
        }

        tail.Reverse();
        return tail.Count == 0 ? [] : tail;
    }
}
