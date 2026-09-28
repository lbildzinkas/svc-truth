namespace SvcTruth.Tests;

/// <summary>
/// Captured and shaped launchctl output samples: a healthy job, a crash-looping one, a never-ran one,
/// a job killed by a signal, and unreadable output. Top-level fields are single-tab indented exactly
/// like the real `launchctl print`.
/// </summary>
public static class SampleData
{
    public const string BridgeLabel = "io.github.example.bridge";
    public const string CacheLabel = "io.github.example.cache";
    public const string GhostLabel = "io.github.example.ghost";

    public const string BridgePlistPath = "/Users/example/Library/LaunchAgents/io.github.example.bridge.plist";
    public const string BridgeStdoutPath = "/Users/example/Library/Logs/bridge/launchd.out.log";
    public const string BridgeStderrPath = "/Users/example/Library/Logs/bridge/launchd.err.log";

    public static readonly string ListOutput = string.Join('\n',
        "PID\tStatus\tLabel",
        $"-\t78\t{BridgeLabel}",
        $"501\t0\t{CacheLabel}",
        $"-\t0\t{GhostLabel}");

    /// <summary>A gui-domain listing where the bridge row carries no usable status either.</summary>
    public static readonly string ListOutputNoStatus = string.Join('\n',
        "PID\tStatus\tLabel",
        $"-\t-\t{BridgeLabel}",
        $"501\t0\t{CacheLabel}",
        $"-\t0\t{GhostLabel}");

    /// <summary>A gui-domain listing where the bridge row shows it running with pid 4242.</summary>
    public static readonly string ListOutputBridgeRunning = string.Join('\n',
        "PID\tStatus\tLabel",
        $"4242\t0\t{BridgeLabel}",
        $"501\t0\t{CacheLabel}",
        $"-\t0\t{GhostLabel}");

    public static readonly string BridgePlist = string.Join('\n',
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
        "<plist version=\"1.0\">",
        "<dict>",
        $"    <key>Label</key>",
        $"    <string>{BridgeLabel}</string>",
        "    <key>ProgramArguments</key>",
        "    <array>",
        "        <string>/usr/local/bin/bridge</string>",
        "    </array>",
        "    <key>KeepAlive</key>",
        "    <true/>",
        "    <key>StandardOutPath</key>",
        $"    <string>{BridgeStdoutPath}</string>",
        "    <key>StandardErrorPath</key>",
        $"    <string>{BridgeStderrPath}</string>",
        "</dict>",
        "</plist>");

    public static string PrintRunning(string label, int pid, long runs, string plistPath) => string.Join('\n',
        $"gui/501/{label} = {{",
        "\tactive count = 1",
        $"\tpath = {plistPath}",
        "\ttype = LaunchAgent",
        "\tstate = running",
        $"\tpid = {pid}",
        $"\truns = {runs}",
        "\tlast exit code = 0",
        "",
        "\tresource coalition = {",
        "\t\tID = 1373",
        "\t\ttype = resource",
        "\t\tstate = active",
        "\t}",
        "",
        "\tdomain = gui/501 [100020]",
        "}");

    public static string PrintCrashLooping(string label, long runs, int exitCode, string plistPath) => string.Join('\n',
        $"gui/501/{label} = {{",
        "\tactive count = 0",
        $"\tpath = {plistPath}",
        "\ttype = LaunchAgent",
        "\tstate = not running",
        $"\truns = {runs}",
        $"\tlast exit code = {exitCode}",
        "",
        "\tdomain = gui/501 [100020]",
        "}");

    public static string PrintKilledBySignal(string label, long runs, int signal) => string.Join('\n',
        $"gui/501/{label} = {{",
        "\tactive count = 0",
        "\tpath = /Users/example/Library/LaunchAgents/signaljob.plist",
        "\tstate = not running",
        $"\truns = {runs}",
        $"\tlast exit code = -{signal}",
        "",
        "\tresource coalition = {",
        "\t\tstate = active",
        "\t}",
        "}");

    /// <summary>
    /// Live-observed shape of a jetsammed job: the print output parses fine but carries only a
    /// "last exit reason" line, never a "last exit code" line.
    /// </summary>
    public static string PrintJetsammed(string label, long runs) => string.Join('\n',
        $"gui/501/{label} = {{",
        "\tactive count = 0",
        "\tpath = /System/Library/LaunchAgents/" + label + ".plist",
        "\ttype = LaunchAgent",
        "\tstate = not running",
        $"\truns = {runs}",
        "\tlast exit reason = jetsam: active limit, 4096 pages",
        "",
        "\tdomain = gui/501 [100020]",
        "}");

    public static string PrintNeverRan(string label) => string.Join('\n',
        $"gui/501/{label} = {{",
        "\tactive count = 0",
        "\tpath = /Users/example/Library/LaunchAgents/ghost.plist",
        "\tstate = not running",
        "\truns = 0",
        "\tlast exit code = (never exited)",
        "}");

    public static string PrintNeverExitedRunning(string label, int pid, string plistPath) => string.Join('\n',
        $"gui/501/{label} = {{",
        "\tstate = running",
        $"\tpid = {pid}",
        "\truns = 1",
        "\tlast exit code = (never exited)",
        $"\tpath = {plistPath}",
        "}");

    /// <summary>A gui-domain listing where the bridge row was killed by a signal (negative status).</summary>
    public static readonly string ListOutputBridgeSignalKilled = string.Join('\n',
        "PID\tStatus\tLabel",
        $"-\t-9\t{BridgeLabel}",
        $"501\t0\t{CacheLabel}",
        $"-\t0\t{GhostLabel}");

    public static string PrintNotFound(string label) => string.Join('\n',
        "Bad request.",
        $"Could not find service \"{label}\" in domain for user gui: 501");

    public static readonly string BridgeStderrTail = string.Join('\n',
        "2026-09-28T11:59:50Z bridge: fatal: connection refused (127.0.0.1:8081)",
        "2026-09-28T11:59:50Z bridge: fatal: connection refused (127.0.0.1:8081)",
        "2026-09-28T11:59:51Z bridge: fatal: connection refused (127.0.0.1:8081)");
}
