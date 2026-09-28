using System.Diagnostics;
using System.Text;

namespace SvcTruth;

/// <summary>
/// Runs commands via System.Diagnostics.Process with a hard timeout. This is the single place in the
/// program that spawns processes, and it refuses launchctl subcommands that mutate service state, so
/// svc-truth stays read-only by construction.
/// </summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    /// <summary>launchctl verbs that change service or domain state; svc-truth must never issue these.</summary>
    public static readonly IReadOnlyList<string> MutatingLaunchctlVerbs =
    [
        "bootstrap", "bootout", "disable", "enable", "kickstart", "kill", "load", "unload",
        "remove", "restart", "submit", "suspend", "resume",
    ];

    public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        AssertReadOnlyLaunchctl(fileName, arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        try
        {
            process.Start();
            process.StandardInput.Close();
        }
        catch (Exception)
        {
            return new CommandResult(null, string.Empty, string.Empty, TimedOut: false, FailedToStart: true);
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        try
        {
            var waited = process.WaitForExit((int)Math.Max(timeout.TotalMilliseconds, 1));
            if (!waited)
            {
                timedOut = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // The process may have exited between the wait timing out and the kill; ignore.
                }
            }

            // The parameterless WaitForExit waits for the async output handlers to drain the pipes;
            // without it AppendLine calls can still be in flight while ToString runs below.
            process.WaitForExit();
        }
        catch (Exception)
        {
            return new CommandResult(null, stdoutBuilder.ToString(), stderrBuilder.ToString(), timedOut, FailedToStart: false);
        }

        return new CommandResult(
            timedOut ? null : process.ExitCode,
            stdoutBuilder.ToString(),
            stderrBuilder.ToString(),
            timedOut,
            FailedToStart: false);
    }

    /// <summary>Throws when asked to run a launchctl invocation that mutates state.</summary>
    public static void AssertReadOnlyLaunchctl(string fileName, IReadOnlyList<string> arguments)
    {
        var name = Path.GetFileName(fileName);
        if (!string.Equals(name, "launchctl", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var verb = arguments.FirstOrDefault() ?? string.Empty;
        if (MutatingLaunchctlVerbs.Contains(verb, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"svc-truth is read-only and refuses to run the launchctl subcommand '{verb}'.");
        }
    }
}
