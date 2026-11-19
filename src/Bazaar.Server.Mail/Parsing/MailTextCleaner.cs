using System.Globalization;
using System.Text;

namespace Bazaar.Server.Mail.Parsing;

/// <summary>
/// Cleans sender-controlled header text before it reaches a screen, a store or a log. Pure; no I/O.
/// </summary>
public static class MailTextCleaner
{
    /// <summary>Longest display name kept, in UTF-16 code units. The rest is dropped, not wrapped.</summary>
    public const int MaxDisplayNameLength = 200;

    /// <summary>
    /// Strips control and format characters (including the bidirectional overrides used to disguise text), collapses runs of
    /// whitespace to one space, trims, and cuts the result to <see cref="MaxDisplayNameLength"/>. Returns null when nothing is left.
    /// </summary>
    public static string? CleanDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(name.Length, MaxDisplayNameLength));
        var lastWasSpace = false;
        foreach (var c in name)
        {
            if (c is '\t' or '\r' or '\n' || char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
            if (builder.Length >= MaxDisplayNameLength)
            {
                break;
            }
        }

        var cleaned = builder.ToString().Trim();
        // A cut can leave half a surrogate pair at the end.
        if (cleaned.Length > 0 && char.IsHighSurrogate(cleaned[^1]))
        {
            cleaned = cleaned[..^1];
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Builds a From header value the address parser reads back as the same name and address: <c>"Name" &lt;address&gt;</c>, with
    /// quotes and backslashes in the name escaped. Without a name it is the bare address; without an address it is null.
    /// </summary>
    public static string? ComposeFromHeader(string? displayName, string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var name = CleanDisplayName(displayName);
        if (name is null)
        {
            return address;
        }

        return $"\"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" <{address}>";
    }
}
