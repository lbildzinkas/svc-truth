using Xunit;

namespace SvcTruth.Tests;

public class RealFileSystemTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("svc-truth-tests-").FullName;

    [Fact]
    public void TailReturnsLastLinesInOrder()
    {
        var path = Path.Combine(_tempDir, "log");
        File.WriteAllLines(path, Enumerable.Range(1, 10).Select(i => $"line {i}"));

        var tail = new RealFileSystem().TryReadTailLines(path, 64 * 1024, maxLines: 5, maxLineLength: 400);

        Assert.NotNull(tail);
        Assert.Equal(new[] { "line 6", "line 7", "line 8", "line 9", "line 10" }, tail);
    }

    [Fact]
    public void TailTrimsLongLines()
    {
        var path = Path.Combine(_tempDir, "long");
        File.WriteAllText(path, new string('a', 600) + "\n");

        var tail = new RealFileSystem().TryReadTailLines(path, 64 * 1024, maxLines: 5, maxLineLength: 400);

        var line = Assert.Single(tail!);
        Assert.Equal(400, line.Length);
    }

    [Fact]
    public void TailOfEmptyFileIsAnEmptyList()
    {
        var path = Path.Combine(_tempDir, "empty");
        File.WriteAllText(path, string.Empty);

        var tail = new RealFileSystem().TryReadTailLines(path, 64 * 1024, maxLines: 5, maxLineLength: 400);

        Assert.NotNull(tail);
        Assert.Empty(tail);
    }

    [Fact]
    public void TailOfMissingFileIsNull()
    {
        var tail = new RealFileSystem().TryReadTailLines(
            Path.Combine(_tempDir, "missing"), 64 * 1024, maxLines: 5, maxLineLength: 400);

        Assert.Null(tail);
    }

    [Fact]
    public void TailOnlyReadsFromTheEndOfLargeFiles()
    {
        var path = Path.Combine(_tempDir, "large");
        var filler = new string('x', 100) + "\n";
        File.WriteAllText(path, string.Concat(Enumerable.Repeat(filler, 1000)) + "the last line\n");

        var tail = new RealFileSystem().TryReadTailLines(path, maxBytesFromEnd: 1000, maxLines: 5, maxLineLength: 400);

        Assert.NotNull(tail);
        Assert.Equal("the last line", tail[^1]);
        Assert.DoesNotContain(tail!, l => l.Length == 0);
    }

    [Fact]
    public void TryReadTextReadsExistingFilesOnly()
    {
        var path = Path.Combine(_tempDir, "text");
        File.WriteAllText(path, "hello");

        var fileSystem = new RealFileSystem();
        Assert.Equal("hello", fileSystem.TryReadText(path, 1024));
        Assert.Null(fileSystem.TryReadText(Path.Combine(_tempDir, "missing"), 1024));
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
