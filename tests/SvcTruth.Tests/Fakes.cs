namespace SvcTruth.Tests;

public sealed class FakeCommandRunner : ICommandRunner
{
    /// <summary>Maps (fileName, arguments) to a handler; each call decrements a per-key counter so tests can serve different outputs for repeated calls.</summary>
    public Func<string, IReadOnlyList<string>, int, CommandResult> Handler { get; set; } =
        (_, _, _) => new CommandResult(0, string.Empty, string.Empty, false, false);

    public List<(string FileName, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

    public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        int callForArguments;
        lock (Invocations)
        {
            callForArguments = Invocations.Count(i => i.FileName == fileName && i.Arguments.SequenceEqual(arguments)) + 1;
            Invocations.Add((fileName, arguments));
        }

        return Handler(fileName, arguments, callForArguments);
    }

    /// <summary>Every launchctl invocation svc-truth asked for, for read-only assertions.</summary>
    public IEnumerable<IReadOnlyList<string>> LaunchctlArguments =>
        Invocations.Where(i => i.FileName == "launchctl").Select(i => i.Arguments);
}

public sealed class FakeFileSystem(Dictionary<string, string> files) : IFileSystem
{
    public List<string> ReadPaths { get; } = [];

    public bool FileExists(string path) => files.ContainsKey(path);

    public string? TryReadText(string path, int maxBytes)
    {
        ReadPaths.Add(path);
        if (!files.TryGetValue(path, out var content) || content.Length > maxBytes)
        {
            return null;
        }

        return content;
    }

    public IReadOnlyList<string>? TryReadTailLines(string path, int maxBytesFromEnd, int maxLines, int maxLineLength)
    {
        ReadPaths.Add(path);
        if (!files.TryGetValue(path, out var content))
        {
            return null;
        }

        if (content.Length == 0)
        {
            return [];
        }

        var lines = content.Split('\n');
        var tail = new List<string>();
        for (var i = lines.Length - 1; i >= 0 && tail.Count < maxLines; i--)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 && i == lines.Length - 1)
            {
                continue;
            }

            tail.Add(line.Length <= maxLineLength ? line : line[..maxLineLength]);
        }

        tail.Reverse();
        return tail;
    }
}

public sealed class FakeClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public DateTimeOffset UtcNow => _now;

    public List<TimeSpan> Slept { get; } = [];

    public void Sleep(TimeSpan delay)
    {
        Slept.Add(delay);
        _now += delay;
    }
}
