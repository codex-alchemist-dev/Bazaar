using System.Text;

namespace Bazaar.Client.Tui.Dashboard;

/// <summary>Plain-text helpers shared by every screen. Text from the server (logs, names) is untrusted and is cleaned before it is drawn.</summary>
public static class TextFit
{
    /// <summary>Replaces control characters (including ESC, so no escape sequence from a log line reaches the terminal) with a dot, and line breaks with a space.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\n' or '\r' or '\t')
            {
                builder.Append(' ');
            }
            else if (char.IsControl(character))
            {
                builder.Append('.');
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>Cleans, then cuts to <paramref name="width"/> characters, ending in "..." when it was cut. No padding.</summary>
    public static string Cut(string? text, int width)
    {
        var clean = Clean(text);
        if (width <= 0)
        {
            return string.Empty;
        }

        if (clean.Length <= width)
        {
            return clean;
        }

        return width <= 3 ? clean[..width] : clean[..(width - 3)] + "...";
    }

    public static string Pad(string? text, int width) => Cut(text, width).PadRight(width);

    /// <summary>"3d 4h", "2h 5m", "7m 3s": uptime in at most two units.</summary>
    public static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{span.Minutes}m {span.Seconds}s";
    }
}
