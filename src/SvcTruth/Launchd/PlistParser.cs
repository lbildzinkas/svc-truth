using System.Xml.Linq;

namespace SvcTruth.Launchd;

/// <summary>Reads the StandardOutPath / StandardErrorPath log paths from a launchd job plist.</summary>
public static class PlistParser
{
    /// <summary>Returns the stdout and stderr log paths declared in the plist XML, or null when absent or unparseable.</summary>
    public static (string? StdoutPath, string? StderrPath) GetLogPaths(string? plistXml)
    {
        if (string.IsNullOrEmpty(plistXml) || plistXml.TrimStart().StartsWith("bplist"))
        {
            return (null, null); // binary plists are not supported; log paths stay unknown
        }

        try
        {
            var document = XDocument.Parse(plistXml);
            var root = document.Root;
            if (root?.Name != "plist")
            {
                return (null, null);
            }

            var dict = root.Element("dict");
            if (dict is null)
            {
                return (null, null);
            }

            string? stdoutPath = null;
            string? stderrPath = null;
            var elements = dict.Elements().ToList();
            for (var i = 0; i + 1 < elements.Count; i++)
            {
                if (elements[i].Name != "key")
                {
                    continue;
                }

                var key = elements[i].Value;
                var valueElement = elements[i + 1];
                if (key == "StandardOutPath" && valueElement.Name == "string")
                {
                    stdoutPath = valueElement.Value;
                }
                else if (key == "StandardErrorPath" && valueElement.Name == "string")
                {
                    stderrPath = valueElement.Value;
                }
            }

            return (stdoutPath, stderrPath);
        }
        catch (Exception)
        {
            return (null, null); // malformed XML: log paths stay unknown
        }
    }
}
