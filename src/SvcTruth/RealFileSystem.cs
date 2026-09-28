using System.Text;

namespace SvcTruth;

/// <summary>Real filesystem reads, tolerant of missing or unreadable files (returns null instead of throwing).</summary>
public sealed class RealFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public string? TryReadText(string path, int maxBytes)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(stream.Length, maxBytes)];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IReadOnlyList<string>? TryReadTailLines(string path, int maxBytesFromEnd, int maxLines, int maxLineLength)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var length = stream.Length;
            if (length == 0)
            {
                return [];
            }

            var bytesToRead = (int)Math.Min(length, maxBytesFromEnd);
            stream.Seek(length - bytesToRead, SeekOrigin.Begin);
            var buffer = new byte[bytesToRead];
            var totalRead = 0;
            while (totalRead < bytesToRead)
            {
                var read = stream.Read(buffer, totalRead, bytesToRead - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, totalRead);
            var lines = text.Split('\n');
            var result = new List<string>();
            for (var i = lines.Length - 1; i >= 0 && result.Count < maxLines; i--)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0 && i == lines.Length - 1)
                {
                    continue; // a trailing newline produces an empty final element; skip it
                }

                result.Add(line.Length <= maxLineLength ? line : line[..maxLineLength]);
            }

            result.Reverse();
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
