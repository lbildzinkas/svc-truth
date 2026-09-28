namespace SvcTruth.Launchd;

/// <summary>One row of <c>launchctl list</c>: the label, current pid (null when not running) and last exit status (negative = killed by signal, null when unknown).</summary>
public sealed record ListEntry(string Label, int? Pid, int? LastExitStatus);

/// <summary>Parses the tab-separated output of <c>launchctl list</c>.</summary>
public static class LaunchctlListParser
{
    /// <summary>"PID\tStatus\tLabel" header line.</summary>
    public const string HeaderLine = "PID\tStatus\tLabel";

    public static IReadOnlyList<ListEntry> Parse(string output)
    {
        var entries = new List<ListEntry>();
        var lines = output.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line == HeaderLine)
            {
                continue;
            }

            var columns = line.Split('\t');
            if (columns.Length < 3)
            {
                continue;
            }

            var pidText = columns[0].Trim();
            var statusText = columns[1].Trim();
            var label = string.Join('\t', columns[2..]).Trim();
            if (label.Length == 0)
            {
                continue;
            }

            int? pid = pidText == "-" || pidText.Length == 0 ? null : ParseInt(pidText);
            int? status = statusText == "-" || statusText.Length == 0 ? null : ParseInt(statusText);
            entries.Add(new ListEntry(label, pid, status));
        }

        return entries;
    }

    private static int? ParseInt(string text) =>
        int.TryParse(text, out var value) ? value : null;
}
