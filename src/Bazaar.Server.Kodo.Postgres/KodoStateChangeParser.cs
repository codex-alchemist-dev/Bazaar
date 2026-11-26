using System.Text;
using Bazaar.Kodo.Journal;

namespace Bazaar.Server.Kodo.Postgres;

/// <summary>Reads the state change a journal record of kind <c>state</c> carries. The server module writes <c>state &lt;from&gt; -&gt; &lt;to&gt;</c>.</summary>
internal static class KodoStateChangeParser
{
    /// <summary>The kind of the records that carry a displayed-state change.</summary>
    public const string StateKind = "state";

    private const string _prefix = "state ";
    private const string _arrow = " -> ";
    private const int _maxName = 32;

    /// <summary>The change, or null when the record is not a well-formed state change.</summary>
    public static KodoStateChange? TryParse(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Kind != StateKind)
        {
            return null;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(record.Payload);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!text.StartsWith(_prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string rest = text[_prefix.Length..];
        int arrow = rest.IndexOf(_arrow, StringComparison.Ordinal);
        if (arrow <= 0)
        {
            return null;
        }

        string from = rest[..arrow];
        string to = rest[(arrow + _arrow.Length)..];
        return IsName(from) && IsName(to) ? new KodoStateChange(record.Sequence, record.At, from, to) : null;
    }

    private static bool IsName(string name) =>
        name.Length is > 0 and <= _maxName && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');
}
