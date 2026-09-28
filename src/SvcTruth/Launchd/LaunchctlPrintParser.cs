using System.Text.RegularExpressions;

namespace SvcTruth.Launchd;

/// <summary>
/// The top-level fields svc-truth reads from <c>launchctl print gui/&lt;uid&gt;/&lt;label&gt;</c>.
/// Runs is null when the output did not contain it; NeverExited is true for "last exit code = (never exited)".
/// </summary>
public sealed record JobPrintInfo(
    string? State,
    int? Pid,
    long? Runs,
    int? LastExitCode,
    bool NeverExited,
    string? PlistPath);

/// <summary>
/// Parses the top-level fields of <c>launchctl print</c> for one job. Top-level fields are indented with a
/// single tab; nested blocks (arguments, resource coalition, ...) are deeper and ignored. Returns null when
/// the output is not a job dictionary (for example launchctl's "Could not find service" error).
/// </summary>
public static partial class LaunchctlPrintParser
{
    [GeneratedRegex(@"^gui/\d+/[^ ]+ = \{$")]
    private static partial Regex HeaderRegex();

    public static JobPrintInfo? Parse(string output)
    {
        var lines = output.Split('\n');
        if (lines.Length == 0 || !HeaderRegex().IsMatch(lines[0].TrimEnd('\r')))
        {
            return null;
        }

        string? state = null;
        int? pid = null;
        long? runs = null;
        int? lastExitCode = null;
        bool neverExited = false;
        string? plistPath = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith('\t') || line.StartsWith("\t\t"))
            {
                continue; // only top-level fields: exactly one tab of indentation
            }

            var field = line[1..];
            if (TryField(field, "state = ", out var value))
            {
                state = value;
            }
            else if (TryField(field, "pid = ", out value))
            {
                pid = int.TryParse(value, out var parsed) ? parsed : null;
            }
            else if (TryField(field, "runs = ", out value))
            {
                runs = long.TryParse(value, out var parsed) ? parsed : null;
            }
            else if (TryField(field, "last exit code = ", out value))
            {
                if (value == "(never exited)")
                {
                    neverExited = true;
                    lastExitCode = null;
                }
                else
                {
                    lastExitCode = ParseExitCode(value);
                }
            }
            else if (TryField(field, "path = ", out value))
            {
                // Real paths point at the job's plist; parenthesised values like
                // "(submitted by runningboardd.413)" belong to app-submitted jobs with no plist.
                plistPath = value.StartsWith('(') ? null : value;
            }
        }

        return new JobPrintInfo(state, pid, runs, lastExitCode, neverExited, plistPath);
    }

    /// <summary>Parses exit statuses like "78", "-15" and launchd's decorated form "78: EX_CONFIG".</summary>
    private static int? ParseExitCode(string value)
    {
        var colon = value.IndexOf(':');
        var numeric = (colon > 0 ? value[..colon] : value).Trim();
        return int.TryParse(numeric, out var parsed) ? parsed : null;
    }

    private static bool TryField(string field, string prefix, out string value)
    {
        if (field.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = field[prefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }
}
