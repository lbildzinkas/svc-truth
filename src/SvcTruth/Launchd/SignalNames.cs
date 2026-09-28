namespace SvcTruth.Launchd;

/// <summary>Signal numbers to names, following the macOS signal(3) list (1-31).</summary>
public static class SignalNames
{
    private static readonly Dictionary<int, string> Map = new()
    {
        [1] = "SIGHUP",
        [2] = "SIGINT",
        [3] = "SIGQUIT",
        [4] = "SIGILL",
        [5] = "SIGTRAP",
        [6] = "SIGABRT",
        [7] = "SIGEMT",
        [8] = "SIGFPE",
        [9] = "SIGKILL",
        [10] = "SIGBUS",
        [11] = "SIGSEGV",
        [12] = "SIGSYS",
        [13] = "SIGPIPE",
        [14] = "SIGALRM",
        [15] = "SIGTERM",
        [16] = "SIGURG",
        [17] = "SIGSTOP",
        [18] = "SIGTSTP",
        [19] = "SIGCONT",
        [20] = "SIGCHLD",
        [21] = "SIGTTIN",
        [22] = "SIGTTOU",
        [23] = "SIGIO",
        [24] = "SIGXCPU",
        [25] = "SIGXFSZ",
        [26] = "SIGVTALRM",
        [27] = "SIGPROF",
        [28] = "SIGWINCH",
        [29] = "SIGINFO",
        [30] = "SIGUSR1",
        [31] = "SIGUSR2",
    };

    /// <summary>The conventional signal name for a number (for example 15 to SIGTERM), or null when unknown.</summary>
    public static string? NameFor(int signalNumber) =>
        Map.TryGetValue(signalNumber, out var name) ? name : null;

    /// <summary>The last-exit status as a human phrase: "killed by SIGTERM (signal 15)" or "exit code 78".</summary>
    public static string Describe(int lastExitStatus)
    {
        if (lastExitStatus < 0)
        {
            var signal = -lastExitStatus;
            var name = NameFor(signal);
            return name is null
                ? $"killed by signal {signal}"
                : $"killed by {name} (signal {signal})";
        }

        return $"exit code {lastExitStatus}";
    }
}
