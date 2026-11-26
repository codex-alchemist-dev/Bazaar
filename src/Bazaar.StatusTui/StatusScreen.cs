using System.Globalization;
using System.Text;
using Bazaar.Server.WebAdmin;

namespace Bazaar.StatusTui;

/// <summary>Draws the status view as plain text. Pure: same input, same output, no colour codes, so a snapshot test can pin it.</summary>
internal static class StatusScreen
{
    public static string Render(AdminStatusView v)
    {
        var sb = new StringBuilder();
        var name = Clean(v.DisplayName);
        var version = Clean(v.Version) + (v.Codename is { Length: > 0 } c ? $" \"{Clean(c)}\"" : string.Empty);
        sb.Append(name).Append("  ").AppendLine(version);
        sb.AppendLine(new string('=', Math.Max(name.Length + version.Length + 2, 20)));
        sb.Append("Up        ").AppendLine(Uptime(v.UptimeSeconds));
        sb.Append("Started   ").AppendLine(v.StartedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        var health = v.Health ?? AdminHealthSummary.Unknown;
        sb.Append("Health    ").AppendLine(Clean(health.State));
        foreach (var check in health.Checks)
        {
            sb.Append("  ").Append(Clean(check.State).PadRight(9)).Append(Clean(check.Name));
            if (check.Detail is { Length: > 0 } d)
            {
                sb.Append(": ").Append(Clean(d));
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Queues");
        if (v.Queues is not { Count: > 0 })
        {
            sb.AppendLine("  none reported");
        }
        else
        {
            var width = v.Queues.Max(q => Clean(q.Name).Length);
            foreach (var q in v.Queues)
            {
                sb.Append("  ").Append(Clean(q.Name).PadRight(width)).Append("  ").AppendLine(q.Depth.ToString(CultureInfo.InvariantCulture));
            }
        }

        sb.AppendLine();
        sb.AppendLine("Providers and host");
        foreach (var line in v.ProviderLines)
        {
            sb.Append("  ").AppendLine(Clean(line));
        }

        return sb.ToString();
    }

    internal static string Uptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        return t.TotalDays >= 1
            ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
            : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
    }

    /// <summary>Server text is data: control characters (including escape sequences) become '?', so it cannot move the cursor or recolour the terminal.</summary>
    internal static string Clean(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : string.Create(s.Length, s, static (dst, src) =>
        {
            for (var i = 0; i < dst.Length; i++)
            {
                dst[i] = char.IsControl(src[i]) ? '?' : src[i];
            }
        });
}
