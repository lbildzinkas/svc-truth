namespace SvcTruth;

/// <summary>
/// Parses the elapsed-time column of <c>ps -o etime= -p &lt;pid&gt;</c>: "MM:SS", "HH:MM:SS" or "D-HH:MM:SS"
/// (single seconds also tolerated defensively). Read-only; used to tell a recovered process from a loop.
/// </summary>
public static class EtimeParser
{
    public static TimeSpan? Parse(string output)
    {
        var text = output.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        TimeSpan days = TimeSpan.Zero;
        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            if (!int.TryParse(text[..dash], out var dayCount))
            {
                return null;
            }

            days = TimeSpan.FromDays(dayCount);
            text = text[(dash + 1)..];
        }

        var parts = text.Split(':');
        if (parts.Length is < 1 or > 3)
        {
            return null;
        }

        var values = new int[3]; // hours, minutes, seconds
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var value))
            {
                return null;
            }

            values[i] = value;
        }

        var (hours, minutes, seconds) = parts.Length switch
        {
            1 => (0, 0, values[0]),
            2 => (0, values[0], values[1]),
            _ => (values[0], values[1], values[2]),
        };

        return days + new TimeSpan(hours, minutes, seconds);
    }
}
